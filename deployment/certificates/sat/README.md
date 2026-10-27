# Autoridades SAT para validación local

Este directorio contiene únicamente certificados públicos de autoridad; no contiene llaves privadas ni certificados personales.

## Cadena incluida

- Raíces de confianza: `ARC5_IES.cer`, `ARC6_IES.cer`.
- Autoridades intermedias: `AC5_SAT.cer`, `AC6_SAT.cer`, `AC7_SAT.cer`.

La cadena del certificado de prueba de Juan Manuel Carrillo Rojo se comprobó sin red como certificado personal → AC5 SAT → ARC5, con resultado válido el 27/10/2026. AC5 vence el 03/05/2027; AC6 y AC7 vencen en 2031. ARC5 vence en 2034 y ARC6 en 2039.

`RequireOcsp=false` evita consultas de revocación, pero conserva las validaciones de vigencia, cadena criptográfica, RFC, uso de firma digital y firma del reto. No habilite `AllowDevelopmentBypass` ni `AllowDevelopmentUntrustedChain` para sustituir una cadena faltante.

Antes de sustituir estos archivos, compare huellas, vigencia y cadena contra una fuente SAT/corporativa autorizada. Nunca agregue `.key`, PFX/P12 ni una e.firma personal a este directorio.
