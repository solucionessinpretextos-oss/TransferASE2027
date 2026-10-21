import { FormEvent, useState } from "react";
import { useAuth } from "../auth/AuthContext";
import { extractRfcFromCertificate } from "../auth/efirma";
import aseLogoWhite from "../assets/ase-logo-white.png";

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
    <main className="relative grid min-h-screen place-items-center overflow-hidden bg-[#07090d] px-4 py-10 text-slate-100">
      <div className="pointer-events-none absolute -left-24 -top-24 h-96 w-96 rounded-full bg-amber-600/10 blur-3xl" />
      <div className="pointer-events-none absolute -bottom-40 right-0 h-[32rem] w-[32rem] rounded-full bg-orange-950/30 blur-3xl" />
      <div className="relative mx-auto grid w-full max-w-5xl overflow-hidden rounded-[2rem] border border-white/10 bg-[#11141a]/90 shadow-[0_32px_100px_rgba(0,0,0,0.55)] backdrop-blur-xl lg:grid-cols-[0.92fr_1.08fr]">
        <section className="relative overflow-hidden border-b border-amber-700/30 bg-gradient-to-br from-[#713f12] via-[#4a260e] to-[#11141a] p-8 sm:p-10 lg:border-b-0 lg:border-r">
          <div className="pointer-events-none absolute -right-20 -top-20 h-64 w-64 rounded-full border border-amber-200/10" />
          <div className="pointer-events-none absolute -right-10 -top-10 h-44 w-44 rounded-full border border-amber-200/10" />
          <img
            src={aseLogoWhite}
            alt="Auditoría Superior del Estado de Sinaloa"
            className="relative h-auto w-64 max-w-full opacity-95"
          />
          <p className="relative mt-10 text-xs font-semibold tracking-[0.28em] text-amber-300">TRANSFER ASE</p>
          <h1 className="relative mt-3 text-3xl font-bold tracking-tight">Dashboard administrativo</h1>
          <p className="relative mt-4 max-w-sm text-sm leading-6 text-amber-50/75">
            El acceso requiere una e.firma vigente y una cuenta autorizada con rol administrativo u operativo.
          </p>
          <div className="relative mt-8 rounded-2xl border border-amber-300/20 bg-black/20 p-5 text-sm text-amber-50/90 backdrop-blur-sm">
            <div className="mb-3 flex h-9 w-9 items-center justify-center rounded-xl bg-amber-300/15 text-amber-200">◆</div>
            <p className="font-semibold">Protección de su e.firma</p>
            <p className="mt-2 leading-6">
              La llave KEY y su contraseña se procesan únicamente en este navegador. El servidor recibe el certificado público y la firma del reto, nunca la llave privada.
            </p>
          </div>
        </section>

        <section className="p-8 sm:p-10">
          <div className="flex items-start justify-between gap-4">
            <div>
              <p className="text-[10px] font-semibold uppercase tracking-[0.24em] text-amber-300/80">Acceso seguro</p>
              <h2 className="mt-2 text-2xl font-semibold tracking-tight">Iniciar sesión con e.firma</h2>
              <p className="mt-2 text-sm text-slate-400">Complete los cuatro datos para validar su identidad.</p>
            </div>
            <span className="mt-1 flex items-center gap-2 rounded-full border border-emerald-400/20 bg-emerald-400/10 px-3 py-1 text-[10px] font-semibold uppercase tracking-wide text-emerald-200">
              <span className="h-1.5 w-1.5 rounded-full bg-emerald-400" /> Seguro
            </span>
          </div>

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
                className="w-full rounded-xl border border-white/10 bg-black/25 px-4 py-3 outline-none transition focus:border-amber-400/70 focus:ring-4 focus:ring-amber-400/5 disabled:opacity-60"
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
                className="block w-full rounded-xl border border-white/10 bg-black/25 text-sm file:mr-4 file:border-0 file:bg-amber-700 file:px-4 file:py-3 file:font-semibold file:text-white hover:file:bg-amber-600 disabled:opacity-60"
              />
            </label>

            <label className="block">
              <span className="mb-2 block text-sm font-medium">Llave privada (.key)</span>
              <input
                type="file"
                accept=".key,application/octet-stream"
                disabled={busy}
                onChange={(event) => setPrivateKey(event.target.files?.[0] ?? null)}
                className="block w-full rounded-xl border border-white/10 bg-black/25 text-sm file:mr-4 file:border-0 file:bg-amber-700 file:px-4 file:py-3 file:font-semibold file:text-white hover:file:bg-amber-600 disabled:opacity-60"
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
                className="w-full rounded-xl border border-white/10 bg-black/25 px-4 py-3 outline-none transition focus:border-amber-400/70 focus:ring-4 focus:ring-amber-400/5 disabled:opacity-60"
                placeholder="Contraseña de la e.firma"
              />
            </label>

            <div className="rounded-xl border border-white/10 bg-black/20 p-4" aria-live="polite">
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
              className="w-full rounded-xl bg-gradient-to-r from-amber-700 to-amber-600 px-5 py-3 font-semibold text-white shadow-lg shadow-amber-950/30 transition hover:from-amber-600 hover:to-amber-500 disabled:cursor-not-allowed disabled:opacity-50"
            >
              {busy ? "Validando e.firma..." : "Ingresar al Dashboard"}
            </button>
          </form>
        </section>
      </div>
    </main>
  );
}
