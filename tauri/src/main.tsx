import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import "@fontsource/alegreya/latin-400.css";
import "@fontsource/alegreya/latin-400-italic.css";
import "@fontsource/alegreya-sans/latin-400.css";
import "@fontsource/alegreya-sans/latin-500.css";
import "@fontsource/alegreya-sans/latin-700.css";
import "@fontsource/alegreya-sans-sc/latin-500.css";
import "./styles.css";
import { createBackend } from "./api";
import { App } from "./App";

(async () => {
  const backend = await createBackend();
  const settings = await backend.getSettings();
  document.documentElement.dataset.theme = settings.theme;
  document.documentElement.classList.add(backend.demo ? "demo" : "tauri");
  createRoot(document.getElementById("root")!).render(
    <StrictMode>
      <App backend={backend} initial={settings} />
    </StrictMode>,
  );
})();
