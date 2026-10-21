import { FormEvent, useState } from "react";
import { useAuth } from "../auth/AuthContext";
import { extractRfcFromCertificate } from "../auth/efirma";

export function EfirmaLogin() {
  const { login } = useAuth();
  const [rfc, setRfc] = useState("");
  const [certificate, setCertificate] = useState<File | null>(null);
  const [privateKey, setPrivateKey] = useState<File | null>(null);
  const [password, setPassword] = useState("");
  const [busy, setBusy] = useState(false);
  const [progress, setProgress] = useState(0);
  const [action, setAction] = useState("Seleccione su certificado y llave privada.");
  const [error, setError] = useState<string | null>(null);

  const chooseCertificate = async (file: File | null) => {
    setCertificate(file);
    setError(null);
    if (!file) return;
    setAction("Leyendo identidad del certificado CER...");
    try {
      const detectedRfc = await extractRfcFromCertificate(file);
      if (detectedRfc) setRfc(detectedRfc);
      setAction(
        detectedRfc
          ? `RFC ${detectedRfc} detectado. Seleccione la llave KEY.`
          : "Capture el RFC asociado al certificado."
      );
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "No fue posible leer el certificado CER.");
      setAction("Corrija el certificado para continuar.");
    }
  };

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    setError(null);
    if (!certificate || !privateKey) {
      setError("Seleccione los archivos CER y KEY de la e.firma.");
      return;
    }

    setBusy(true);
    setProgress(5);
    setAction("Preparando inicio de sesión seguro...");
    try {
      await login(
        { rfc, certificate, privateKey, password },
        (percentage, currentAction) => {
          setProgress(percentage);
          setAction(currentAction);
        }
      );
      setPassword("");
    } catch (cause) {
      setProgress(0);
      setPassword("");
      setError(cause instanceof Error ? cause.message : "No fue posible iniciar sesión.");
      setAction("El acceso fue detenido de forma segura.");
    } finally {
      setBusy(false);
    }
  };

  return (
    <main className="min-h-screen bg-slate-950 px-4 py-10 text-slate-100">
      <div className="mx-auto grid max-w-5xl overflow-hidden rounded-3xl border border-slate-800 bg-slate-900 shadow-2xl lg:grid-cols-[0.9fr_1.1fr]">
        <section className="border-b border-amber-700/40 bg-gradient-to-br from-amber-950 via-amber-900 to-slate-950 p-8 lg:border-b-0 lg:border-r">
          <p className="text-sm font-semibold tracking-[0.22em] text-amber-300">TRANSFER ASE</p>
          <h1 className="mt-4 text-3xl font-bold">Dashboard administrativo</h1>
          <p className="mt-4 text-sm leading-6 text-amber-100/80">
            El acceso requiere una e.firma vigente y una cuenta autorizada con rol administrativo u operativo.
          </p>
          <div className="mt-8 rounded-2xl border border-amber-500/30 bg-black/20 p-5 text-sm text-amber-50/90">
            <p className="font-semibold">Protección de su e.firma</p>
            <p className="mt-2 leading-6">
              La llave KEY y su contraseña se procesan únicamente en este navegador. El servidor recibe el certificado público y la firma del reto, nunca la llave privada.
            </p>
          </div>
        </section>

        <section className="p-8">
          <h2 className="text-2xl font-semibold">Iniciar sesión con e.firma</h2>
          <p className="mt-2 text-sm text-slate-400">Complete los cuatro datos para validar su identidad.</p>

          <form className="mt-7 space-y-5" onSubmit={submit}>
            <label className="block">
              <span className="mb-2 block text-sm font-medium">RFC</span>
              <input
                value={rfc}
                onChange={(event) => setRfc(event.target.value.toUpperCase())}
                maxLength={13}
                autoCapitalize="characters"
                autoComplete="username"
                disabled={busy}
                className="w-full rounded-xl border border-slate-700 bg-slate-950 px-4 py-3 outline-none focus:border-amber-500 disabled:opacity-60"
                placeholder="RFC del titular"
              />
            </label>

            <label className="block">
              <span className="mb-2 block text-sm font-medium">Certificado (.cer)</span>
              <input
                type="file"
                accept=".cer,application/pkix-cert"
                disabled={busy}
                onChange={(event) => void chooseCertificate(event.target.files?.[0] ?? null)}
                className="block w-full rounded-xl border border-slate-700 bg-slate-950 text-sm file:mr-4 file:border-0 file:bg-amber-700 file:px-4 file:py-3 file:font-semibold file:text-white hover:file:bg-amber-600 disabled:opacity-60"
              />
            </label>

            <label className="block">
              <span className="mb-2 block text-sm font-medium">Llave privada (.key)</span>
              <input
                type="file"
                accept=".key,application/octet-stream"
                disabled={busy}
                onChange={(event) => setPrivateKey(event.target.files?.[0] ?? null)}
                className="block w-full rounded-xl border border-slate-700 bg-slate-950 text-sm file:mr-4 file:border-0 file:bg-amber-700 file:px-4 file:py-3 file:font-semibold file:text-white hover:file:bg-amber-600 disabled:opacity-60"
              />
            </label>

            <label className="block">
              <span className="mb-2 block text-sm font-medium">Contraseña de la llave privada</span>
              <input
                type="password"
                value={password}
                onChange={(event) => setPassword(event.target.value)}
                autoComplete="current-password"
                disabled={busy}
                className="w-full rounded-xl border border-slate-700 bg-slate-950 px-4 py-3 outline-none focus:border-amber-500 disabled:opacity-60"
                placeholder="Contraseña de la e.firma"
              />
            </label>

            <div className="rounded-xl border border-slate-700 bg-slate-950/70 p-4" aria-live="polite">
              <div className="flex justify-between gap-4 text-xs">
                <span>{action}</span>
                <span className="font-semibold tabular-nums">{progress}%</span>
              </div>
              <div className="mt-3 h-2 overflow-hidden rounded-full bg-slate-800">
                <div
                  className="h-full bg-emerald-500 transition-[width] duration-300"
                  style={{ width: `${progress}%` }}
                />
              </div>
            </div>

            {error && (
              <div className="rounded-xl border border-rose-700 bg-rose-950/60 px-4 py-3 text-sm text-rose-100" role="alert">
                <p className="font-semibold">No fue posible iniciar sesión</p>
                <p className="mt-1">{error}</p>
              </div>
            )}

            <button
              type="submit"
              disabled={busy || !rfc.trim() || !certificate || !privateKey || !password}
              className="w-full rounded-xl bg-emerald-600 px-5 py-3 font-semibold text-white hover:bg-emerald-500 disabled:cursor-not-allowed disabled:opacity-50"
            >
              {busy ? "Validando e.firma..." : "Ingresar al Dashboard"}
            </button>
          </form>
        </section>
      </div>
    </main>
  );
}
