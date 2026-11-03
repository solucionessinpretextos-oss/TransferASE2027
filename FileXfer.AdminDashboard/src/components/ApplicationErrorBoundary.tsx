import { Component, ReactNode } from "react";
import aseLogoWhite from "../assets/ase-logo-white.png";

/** Keeps an unexpected rendering failure visible without exposing session or server details. */
export class ApplicationErrorBoundary extends Component<{ children: ReactNode }, { failed: boolean }> {
  state = { failed: false };

  static getDerivedStateFromError() {
    return { failed: true };
  }

  render() {
    if (!this.state.failed) return this.props.children;

    return (
      <main className="min-h-screen bg-[#07090d] px-6 py-12 text-slate-100 grid place-items-center">
        <section role="alert" className="w-full max-w-xl rounded-2xl border border-amber-300/30 bg-[#11141a] p-8 shadow-xl">
          <img src={aseLogoWhite} alt="Auditoría Superior del Estado de Sinaloa" className="mb-8 h-auto w-56" />
          <h1 className="text-2xl font-semibold text-white">No se pudo mostrar el Dashboard</h1>
          <p className="mt-4 text-sm leading-6 text-slate-300">
            Ocurrió un problema al mostrar esta pantalla. Vuelva a intentarlo o abra la página principal.
            Si continúa, informe a soporte qué estaba haciendo y la hora del problema.
          </p>
          <div className="mt-6 flex flex-wrap gap-3">
            <button type="button" onClick={() => this.setState({ failed: false })}
              className="rounded-lg border border-amber-300/40 bg-amber-300/10 px-4 py-3 text-sm font-semibold text-amber-100">
              Volver a intentar
            </button>
            <a href={import.meta.env.BASE_URL}
              className="rounded-lg border border-white/20 px-4 py-3 text-sm text-slate-100">
              Abrir página principal
            </a>
          </div>
          <p className="mt-5 text-xs leading-5 text-slate-400">Estas acciones no eliminan archivos ni reinician estadísticas.</p>
        </section>
      </main>
    );
  }
}
