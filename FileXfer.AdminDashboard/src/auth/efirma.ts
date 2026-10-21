// Procesamiento local de e.firma para el Dashboard administrativo.
// La llave privada y su contrasena nunca se envian al servidor.

import { hextopem, KEYUTIL, KJUR, X509 } from "jsrsasign";

const RFC_PATTERN = /^[A-Z&Ñ]{3,4}[0-9]{6}[A-Z0-9]{3}$/;
const MAX_CERTIFICATE_BYTES = 48 * 1024;
const MAX_PRIVATE_KEY_BYTES = 64 * 1024;

export interface EfirmaCredentials {
  rfc: string;
  certificate: File;
  privateKey: File;
  password: string;
}

export interface LoginChallenge {
  nonceId: string;
  nonce: string;
}

export interface SignedLoginRequest {
  rfc: string;
  cerBase64: string;
  nonceId: string;
  nonce: string;
  signatureVersion: "1";
  signatureBase64: string;
  deviceId: string;
}

function bytesToHex(bytes: Uint8Array): string {
  let result = "";
  for (const value of bytes) result += value.toString(16).padStart(2, "0");
  return result;
}

function hexToBase64(hex: string): string {
  let binary = "";
  for (let index = 0; index < hex.length; index += 2) {
    binary += String.fromCharCode(Number.parseInt(hex.slice(index, index + 2), 16));
  }
  return btoa(binary);
}

function normalizeRfc(value: string): string {
  return value.trim().toUpperCase();
}

function requireFile(file: File, extension: string, maxBytes: number, label: string): void {
  if (!file.name.toLowerCase().endsWith(extension)) {
    throw new Error(`${label} debe tener extensión ${extension}.`);
  }
  if (file.size <= 0 || file.size > maxBytes) {
    throw new Error(`${label} está vacío o excede el tamaño permitido.`);
  }
}

function createCanonicalPayload(
  nonceId: string,
  nonce: string,
  rfc: string,
  deviceId: string
): string {
  return [
    "FILEXFER-LOGIN-V1",
    nonceId.trim().toLowerCase(),
    nonce,
    normalizeRfc(rfc),
    deviceId
  ].join("\n");
}

function findCertificateRfc(certificate: X509): string | null {
  const attributes = certificate.getSubject().array.flat();
  const preferredTypes = new Set([
    "2.5.4.45",
    "OID.2.5.4.45",
    "uniqueIdentifier",
    "UID",
    "serialNumber",
    "SERIALNUMBER"
  ]);

  for (const attribute of attributes) {
    if (!preferredTypes.has(attribute.type)) continue;
    const match = attribute.value.toUpperCase().match(/[A-Z&Ñ]{3,4}[0-9]{6}[A-Z0-9]{3}/);
    if (match) return match[0];
  }

  const fallback = certificate
    .getSubjectString()
    .toUpperCase()
    .match(/[A-Z&Ñ]{3,4}[0-9]{6}[A-Z0-9]{3}/);
  return fallback?.[0] ?? null;
}

function parseCertificate(certificateHex: string): { certificate: X509; pem: string } {
  try {
    const pem = hextopem(certificateHex, "CERTIFICATE");
    const certificate = new X509();
    certificate.readCertPEM(pem);
    certificate.getPublicKey();
    return { certificate, pem };
  } catch {
    throw new Error("El archivo CER no contiene un certificado válido.");
  }
}

export async function extractRfcFromCertificate(file: File): Promise<string | null> {
  requireFile(file, ".cer", MAX_CERTIFICATE_BYTES, "El certificado CER");
  const bytes = new Uint8Array(await file.arrayBuffer());
  try {
    return findCertificateRfc(parseCertificate(bytesToHex(bytes)).certificate);
  } finally {
    bytes.fill(0);
  }
}

export async function createSignedLoginRequest(
  credentials: EfirmaCredentials,
  challenge: LoginChallenge,
  deviceId: string
): Promise<SignedLoginRequest> {
  const rfc = normalizeRfc(credentials.rfc);
  if (!RFC_PATTERN.test(rfc)) throw new Error("El RFC no tiene un formato válido.");
  if (!credentials.password) throw new Error("Capture la contraseña de la llave privada.");
  requireFile(credentials.certificate, ".cer", MAX_CERTIFICATE_BYTES, "El certificado CER");
  requireFile(credentials.privateKey, ".key", MAX_PRIVATE_KEY_BYTES, "La llave KEY");

  const certificateBytes = new Uint8Array(await credentials.certificate.arrayBuffer());
  const privateKeyBytes = new Uint8Array(await credentials.privateKey.arrayBuffer());

  try {
    const certificateHex = bytesToHex(certificateBytes);
    const { certificate, pem: certificatePem } = parseCertificate(certificateHex);
    const certificateRfc = findCertificateRfc(certificate);
    if (!certificateRfc || certificateRfc !== rfc) {
      throw new Error("El RFC capturado no coincide con el certificado CER.");
    }

    let privateKey: ReturnType<typeof KEYUTIL.getKey>;
    try {
      const encryptedKeyPem = hextopem(bytesToHex(privateKeyBytes), "ENCRYPTED PRIVATE KEY");
      privateKey = KEYUTIL.getKey(encryptedKeyPem, credentials.password);
    } catch {
      throw new Error("La contraseña es incorrecta o el archivo KEY no es válido.");
    }

    const payload = createCanonicalPayload(
      challenge.nonceId,
      challenge.nonce,
      rfc,
      deviceId
    );

    const signer = new KJUR.crypto.Signature({ alg: "SHA256withRSA" });
    signer.init(privateKey);
    signer.updateString(payload);
    const signatureHex = signer.sign();

    // Esta verificación local evita enviar intentos cuando CER y KEY no forman pareja.
    const verifier = new KJUR.crypto.Signature({ alg: "SHA256withRSA" });
    verifier.init(certificatePem);
    verifier.updateString(payload);
    if (!verifier.verify(signatureHex)) {
      throw new Error("La llave KEY no corresponde al certificado CER.");
    }

    return {
      rfc,
      cerBase64: hexToBase64(certificateHex),
      nonceId: challenge.nonceId,
      nonce: challenge.nonce,
      signatureVersion: "1",
      signatureBase64: hexToBase64(signatureHex),
      deviceId
    };
  } finally {
    // JavaScript no garantiza borrado físico, pero se limpia el material binario tan pronto termina.
    privateKeyBytes.fill(0);
    certificateBytes.fill(0);
  }
}
