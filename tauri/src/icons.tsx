// Iconos de línea propios (caja 24×24), los mismos trazos que la versión de escritorio anterior.
const PATHS = {
  d20:
    "M12 2l8.5 5v10L12 22l-8.5-5V7z M12 7.5l5 8H7z M3.5 7L12 7.5L20.5 7 M3.5 7L7 15.5L3.5 17 M7 15.5L12 22L17 15.5L20.5 17 M20.5 7L17 15.5",
  sliders: "M4 21v-7M4 10V3M12 21v-9M12 8V3M20 21v-5M20 12V3M1 14h6M9 8h6M17 16h6",
  send: "M22 2L11 13M22 2l-7 20-4-9-9-4z",
  caps: "M3 5h10M8 5v14M18 5v14M15 8l3-3 3 3M15 16l3 3 3-3",
  filter: "M3 4h18l-7 8.5V19l-4 2v-8.5z",
  check: "M5 12.5l4.5 4.5L19 7.5",
  pin: "M9 4h6l-1 6 3 3v1H7v-1l3-3zM12 14v7",
  minus: "M5 12h14",
  close: "M6 6l12 12M18 6L6 18",
  down: "M12 5v14M6 13l6 6 6-6",
  compact: "M4 9h16M4 15h16M9 4v5M15 15v5",
  dice: "M5 4h14a1 1 0 011 1v14a1 1 0 01-1 1H5a1 1 0 01-1-1V5a1 1 0 011-1z M9 9h.01M15 15h.01M12 12h.01",
} as const;

export type IconName = keyof typeof PATHS;

export function Icon({
  name,
  size = 18,
  stroke = 1.8,
  className,
}: {
  name: IconName;
  size?: number;
  stroke?: number;
  className?: string;
}) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={stroke}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      className={className}
    >
      <path d={PATHS[name]} />
    </svg>
  );
}
