import type { ThemeId } from "./types";

// Los colores de cada paleta están en styles.css ([data-theme=…]); aquí solo el nombre que se ve en Ajustes.
export const THEMES: Array<{ id: ThemeId; name: string; desc: string }> = [
  { id: "taberna", name: "Taberna", desc: "Madera y latón" },
  { id: "pizarra", name: "Pizarra", desc: "Gris azulado y naranja" },
  { id: "arcano", name: "Arcano", desc: "Índigo y verde azulado" },
  { id: "bosque", name: "Bosque", desc: "Verde pino y oro" },
];
