import { beforeAll, describe, expect, it } from "vitest";
import { KEYUTIL, KJUR, pemtohex } from "jsrsasign";
import { createSignedLoginRequest, extractRfcFromCertificate } from "./efirma";

const RFC = "CARJ721209SD2";
const PASSWORD = "Prueba.1234";
let certificateFile: File;
let privateKeyFile: File;

function hexToBytes(hex: string): Uint8Array<ArrayBuffer> {
  const bytes = new Uint8Array(new ArrayBuffer(hex.length / 2));
  for (let index = 0; index < bytes.length; index += 1) {
    bytes[index] = Number.parseInt(hex.slice(index * 2, index * 2 + 2), 16);
  }
  return bytes;
}

beforeAll(() => {
  const keyPair = KEYUTIL.generateKeypair("RSA", 2048);
  const certificate = new KJUR.asn1.x509.Certificate({
    version: 3,
    serial: { int: 1 },
    issuer: { str: "/C=MX/O=ASE/CN=Prueba" },
    subject: { str: `/C=MX/O=ASE/CN=JUAN MANUEL/UID=${RFC}` },
    notbefore: "261001000000Z",
    notafter: "271001000000Z",
    sbjpubkey: keyPair.pubKeyObj,
    sigalg: "SHA256withRSA",
    cakey: keyPair.prvKeyObj,
    ext: [{ extname: "keyUsage", names: ["digitalSignature"] }]
  });

  const certificatePem = certificate.getPEM();
  const privateKeyPem = KEYUTIL.getPEM(keyPair.prvKeyObj, "PKCS8PRV", PASSWORD);
  certificateFile = new File([hexToBytes(pemtohex(certificatePem))], "prueba.cer");
  privateKeyFile = new File([hexToBytes(pemtohex(privateKeyPem))], "prueba.key");
});

describe("e.firma administrativa", () => {
  it("extrae el RFC y firma el reto sin enviar la llave privada", async () => {
    expect(await extractRfcFromCertificate(certificateFile)).toBe(RFC);

    const request = await createSignedLoginRequest(
      {
        rfc: RFC,
        certificate: certificateFile,
        privateKey: privateKeyFile,
        password: PASSWORD
      },
      { nonceId: "0123456789abcdef0123456789abcdef", nonce: "reto-seguro" },
      "dashboard-prueba"
    );

    expect(request.rfc).toBe(RFC);
    expect(request).not.toHaveProperty("password");
    expect(request).not.toHaveProperty("privateKey");
    expect(request.signatureVersion).toBe("1");

    const verifier = new KJUR.crypto.Signature({ alg: "SHA256withRSA" });
    verifier.init(KEYUTIL.getKey(hexToCertificatePem(request.cerBase64)));
    verifier.updateString([
      "FILEXFER-LOGIN-V1",
      request.nonceId,
      request.nonce,
      RFC,
      request.deviceId
    ].join("\n"));
    expect(verifier.verify(base64ToHex(request.signatureBase64))).toBe(true);
  });

  it("rechaza una contraseña incorrecta", async () => {
    await expect(createSignedLoginRequest(
      {
        rfc: RFC,
        certificate: certificateFile,
        privateKey: privateKeyFile,
        password: "incorrecta"
      },
      { nonceId: "0123456789abcdef0123456789abcdef", nonce: "reto-seguro" },
      "dashboard-prueba"
    )).rejects.toThrow("contraseña");
  });
});

function hexToCertificatePem(base64: string): string {
  const lines = base64.match(/.{1,64}/g) ?? [];
  return `-----BEGIN CERTIFICATE-----\n${lines.join("\n")}\n-----END CERTIFICATE-----`;
}

function base64ToHex(base64: string): string {
  const binary = atob(base64);
  let hex = "";
  for (let index = 0; index < binary.length; index += 1) {
    hex += binary.charCodeAt(index).toString(16).padStart(2, "0");
  }
  return hex;
}
