# Traductor NWN:EE (EN/ES)

Herramienta en C# (.NET 8, WinForms) para traducir en tiempo real el chat de
Neverwinter Nights: Enhanced Edition.

## Qué hace

- Vigila el log `nwclientLog1.txt` (en tu carpeta Documentos, también si está
  redirigida a OneDrive) y traduce los mensajes nuevos de inglés a español.
- Tres pestañas: **Ambos** (traducción y debajo el original), **Inglés** (al
  instante, sin esperar a la traducción) y **Español**. `Ctrl+1/2/3` cambia de pestaña.
- Escribes en español, pulsas Enter (o el botón de enviar) y se traduce al inglés,
  se escribe en el chat del juego y se envía solo. Tus mensajes aparecen a la
  derecha: "✓ enviado" cuando el juego lo confirma en su log, "sin confirmar" si
  no aparece (entonces pulsa Enter en el juego) o "no enviado" si falló.
- El envío simula el teclado con códigos de tecla reales (no `SendKeys`), así el
  juego reconoce el Enter. El texto viaja como caracteres Unicode: llega bien con
  cualquier distribución de teclado (ñ, tildes, ¿ ¡) y nunca dispara atajos del juego.
- Traduce con **Microsoft Translator** (gratis hasta 2 millones de caracteres al
  mes) y, cuando se acaba esa cuota o no hay clave, con **Google Translate**.
  Ver [Qué traductor usar](#qué-traductor-usar).

## Escribir en el juego

- **Enter** traduce y envía. **Ctrl+Enter** envía el texto tal cual, sin traducir
  (nombres, frases que ya tienes en inglés, comandos).
- **Esc** cancela el mensaje que está esperando a salir y lo devuelve al campo
  para corregirlo (para eso sirve la espera de "Esperar antes de enviar").
- **Comandos de chat**: `/p`, `/s`, `/w`, `/dm`, `/tk` y `/t "Nombre"` se mantienen
  y solo se traduce el mensaje: `/t "Seraphine" nos vemos luego` → `/t "Seraphine" see you later`.
  Los demás comandos (`/roll`, `!comando` del servidor…) se envían tal cual.
- Tras enviar, vuelve al traductor para seguir escribiendo (se puede desactivar
  en Ajustes → "Después de enviar").
- **Ctrl+Mayús+Espacio** (tecla rápida global): desde el juego salta al campo de
  escribir del traductor; si envías desde ahí, te deja de nuevo en el juego. Desde
  el traductor, la misma tecla vuelve al juego.
- Las comillas tipográficas, guiones largos, emojis y demás caracteres que NWN no
  sabe mostrar se cambian por su equivalente simple o se quitan.
- Si no se pudo traducir o enviar, el mensaje vuelve al campo de texto.

## La interfaz

- **El chat se lee como una crónica**: hora, nombre en el color de acento,
  mensaje en letra serif y el original debajo, más pequeño. Las acciones entre
  asteriscos (`*se sienta*`) salen en cursiva y en otro color.
- **Etiquetas de canal**: privado, susurro, grupo, grito, DM y servidor. Los
  mensajes que no se pudieron traducir a tiempo llevan la etiqueta "sin traducir".
- **Indicador de estado** arriba a la derecha: traductor activo, mensajes en
  cola y la pausa en segundos cuando la API está limitando.
- **Ajustes** (botón de la cabecera, `Esc` para cerrar): traductor y su clave,
  palabras que no se traducen, espera antes de enviar, volver al traductor, tecla
  rápida, qué se traduce, aviso de privados, colores, ventana y transparencia.
- **Ventana compacta**: sin título, pestañas ni línea de ayuda, para dejarla
  pequeña encima del juego.
- **Transparencia**: la ventana se vuelve semitransparente cuando no la estás
  usando (40–100 %), para ver el juego a través; al volver a ella, se ve entera.
- **Mensajes privados**: suena un aviso y la ventana parpadea en la barra de
  tareas cuando te llega un `[Tell]`.
- **Cuatro paletas de color**: Taberna (madera y latón), Pizarra (gris azulado
  y naranja), Arcano (índigo y verde azulado) y Bosque (verde pino y oro).
- Barra de título del color del tema en Windows 11 y barras de desplazamiento
  oscuras. Fuentes Sitka y Bahnschrift, que vienen con Windows.
- Todos los controles se dibujan a mano: ya no hace falta Guna ni FontAwesome.

## Muchos mensajes y bloqueos de la API

- **Agrupa** todas las líneas que llegan juntas en una sola petición.
- **Caché**: los mensajes repetidos no vuelven a pedirse; tu propio mensaje,
  cuando el juego lo escribe en el log, se muestra en español sin gastar petición.
- Si la API devuelve 429 (límite de peticiones), se hace una **pausa con espera
  progresiva** (respetando `Retry-After`). Mientras tanto los mensajes se van
  acumulando y salen todos juntos al acabar la pausa.
- Si un mensaje lleva más de 15 s esperando, se muestra **sin traducir** para que
  el chat no se quede atrás respecto al juego.
- **Solo el chat de jugadores** (activado por defecto): el combate y los mensajes
  del sistema aparecen sin traducir en la pestaña Inglés. Es lo que más
  peticiones ahorra en las peleas.
- **Orden de traductores: Microsoft → DeepL → Google.** Se usa el elegido; si
  no funciona (sin cuota, clave no válida, en pausa o con errores del servidor),
  el mismo mensaje pasa enseguida al siguiente que tenga clave y, si ninguno de
  los de pago funciona, a Google. No se pierde ningún mensaje.
- **Respaldo con Google opcional** (Ajustes → Respaldo, activado por defecto).
  Si lo desactivas y fallan Microsoft y DeepL, los mensajes se muestran sin
  traducir (con un aviso) y lo que escribes no se envía hasta que lo mandes sin
  traducir con Ctrl+Enter. En Ajustes se ve el orden que se está usando.
- Cuando se acaba la cuota gratuita de Microsoft o DeepL se avisa una vez y cada
  hora se vuelve a probar; en cuanto se renueva, se vuelve a usar solo. Una clave
  que deja de valer no se vuelve a usar hasta que la corrijas.
- Y al revés: si Google te bloquea y tienes otra clave guardada, se usa esa
  mientras dura el bloqueo.
- El indicador de arriba dice qué traductor se está usando y por qué no el
  principal (por ejemplo "Google Translate · Microsoft sin cuota").
- Google se usa como mucho una vez por segundo (lo que llegue entre medias va en
  la misma petición). Sus bloqueos con 403 o con la página de "tráfico inusual"
  ahora también se respetan con una pausa (antes se seguía insistiendo y el
  bloqueo duraba más).
- Al empezar una partida nueva (el juego rota el log) ya **no** se vuelve a
  traducir la sesión anterior entera.

## Calidad de la traducción

- **Abreviaturas de chat**: `u`, `ty`, `brb`, `idk`, `omw`, `im`, `dont`… se
  escriben completas antes de traducir (en pantalla ves el original). `lol`, `gg`,
  `afk`, `ooc`… se dejan tal cual.
- **No traducir** (Ajustes): nombres, lugares o jerga del servidor separados por
  comas (`Seraphine, Kael, Moonstone Mask`). Se mantienen exactamente igual en
  los dos sentidos.

## Qué traductor usar

El traductor por defecto es **Microsoft Translator**. Si tienes también clave de
DeepL, es el siguiente; **Google** queda como último respaldo (se puede
desactivar en Ajustes → Respaldo).

| Traductor | Coste | Notas |
|---|---|---|
| **Microsoft Translator** (principal) | Gratis hasta 2 millones de caracteres al mes con el plan F0 | Oficial y estable. Cuando se acaba la cuota responde con un error (no cobra) y la app pasa a Google. |
| **Google Translate** (respaldo) | Gratis, sin clave | Servicio público no oficial: puede bloquear si recibe muchas peticiones seguidas. |
| **DeepL** | Ha cambiado sus planes; revisa en su web qué ofrece ahora | Muy buena calidad. Opcional. |

2 millones de caracteres son unas 30.000 líneas de chat de tamaño medio. En
Ajustes verás un contador aproximado de lo que lleva enviado la app este mes
(Microsoft no da el consumo exacto con la clave).

**Cómo conseguir la clave de Microsoft Translator:**

1. Crea una cuenta en https://portal.azure.com. Azure pide una tarjeta para
   verificar que eres tú, pero no cobra nada al registrarte.
2. *Crear un recurso* → busca **Translator** → *Crear*.
3. Plan de tarifa: **Free F0** (importante: el plan S1 es de pago). Región:
   **Global**.
4. Cuando esté creado, abre *Claves y punto de conexión* y copia la **Clave 1**.
5. En el traductor: Ajustes → Traductor → **Microsoft**, pega la clave (la región
   déjala vacía si elegiste Global) y pulsa **Comprobar**.

**Importante sobre la cuenta de Azure:** la cuenta gratuita de prueba dura 30
días. Pasado ese tiempo, Azure pide pasarla a *pago por uso*; si no lo haces, la
cuenta y sus recursos se desactivan y la clave deja de funcionar (la app seguiría
traduciendo con Google). Pasar a pago por uso no cobra nada mientras solo tengas
el recurso Translator con el plan F0.

## Cómo generar el .exe

**Sin instalar nada:** GitHub lo compila solo. Cada vez que cambia el código en
`master`, el flujo *Compilar exe* (pestaña **Actions**) genera dos versiones y las
guarda en `dist\`. También se puede lanzar a mano desde Actions → Compilar exe →
*Run workflow*.

- `TraductorNWN.exe` (unos 65 MB): lleva .NET dentro y funciona en cualquier
  Windows de 64 bits sin instalar nada.
- `TraductorNWN-ligero.exe` (menos de 1 MB): el mismo programa sin .NET dentro.
  Necesita el **Runtime de escritorio de .NET** 8 o posterior (mejor el 10, que
  tiene soporte hasta 2028): https://dotnet.microsoft.com/download/dotnet/10.0 →
  *.NET Desktop Runtime* → Windows x64. Si falta, al abrirlo Windows ofrece
  descargarlo. Es la versión que menos recelo da a los antivirus.

Antes de guardarlos, el flujo comprueba que cada uno arranca solo (sin DLL al
lado) y los analiza con Microsoft Defender actualizado; el resultado queda en
`dist\analisis-antivirus.txt`.

**En tu PC:**

1. Instala el SDK de .NET 8: https://dotnet.microsoft.com/download/dotnet/8.0 (elige "SDK").
2. Haz doble clic en `build.bat`.

## Configuración

Se guarda en `%AppData%\TraductorNWN\config.json` (todo se cambia desde Ajustes):

- `DeepLKeyCifrada`: la clave de DeepL, cifrada para tu usuario de Windows (la
  versión anterior la guardaba en texto plano junto al .exe; se migra sola).
- `Proveedor`: `Microsoft` (por defecto), `Google` o `DeepL`.
- `MicrosoftKeyCifrada`, `MicrosoftRegion`: clave (cifrada) y región de Microsoft Translator.
- `GoogleDeRespaldo`: usar Google cuando Microsoft y DeepL no funcionan (`true` por defecto).
- `MicrosoftMes`, `MicrosoftCaracteres`: contador aproximado de caracteres enviados a Microsoft este mes.
- `NoTraducir`: palabras que no se traducen.
- `VolverAlTraductor`, `TeclaRapida`, `AvisarPrivados`, `Compacto`.
- `OpacidadInactiva`: opacidad (%) cuando no estás usando la ventana (100 = sin transparencia).
- `Tema`: `taberna`, `pizarra`, `arcano` o `bosque`.
- `Mayusculas`, `FiltrarSistema`, `SiempreEncima`.
- `RetardoEnvioMs`: espera antes de escribir tu mensaje en el juego (0 a 5000; 2000 por defecto).
- `Ventana`: posición y tamaño de la ventana.

Los errores inesperados se registran en `%AppData%\TraductorNWN\errores.log`.

## Notas

- **El juego tiene que guardar el chat en su log.** En NWN:EE se activa en
  `Documentos\Neverwinter Nights\settings.tml`, sección `[game.log.chat.all]`,
  con `enabled = true` (con el juego cerrado). Deja `[game.log.chat.text]` en
  `enabled = false` o cada mensaje saldrá dos veces. Si no está activado, el
  traductor te avisa al arrancar.

- **Aviso "Windows protegió su PC" (SmartScreen).** Sale con los programas
  descargados de internet que no tienen firma digital o que todavía son poco
  conocidos. Para tu propio equipo: clic derecho en `TraductorNWN.exe` →
  *Propiedades* → marca **Desbloquear** → *Aceptar* (una vez por cada descarga).
  También vale "Más información" → "Ejecutar de todas formas". Firmar el
  programa requiere comprar un certificado y, aun firmado, Windows sigue
  avisando hasta que el programa gana reputación con las descargas.
- **El antivirus lo marca como sospechoso.** Es un falso positivo habitual con
  programas nuevos, sin firma y que simulan el teclado (para escribir en el
  juego). Se ha quitado lo que más suele disparar las alarmas: el .exe ya no saca
  DLL a la carpeta temporal al arrancar ni lee el estado del teclado de todo el
  sistema. Si aun así salta:
  1. Prueba `TraductorNWN-ligero.exe`.
  2. Mira en `dist\analisis-antivirus.txt` qué dice Microsoft Defender.
  3. Envía el .exe a tu antivirus como falso positivo. Microsoft:
     https://www.microsoft.com/en-us/wdsi/filesubmission → *Software developer* →
     *Incorrectly detected as malware/malicious*. Suelen revisarlo en pocos días y
     desde entonces deja de saltar para todo el mundo (hay que repetirlo con cada
     versión nueva del .exe si vuelve a saltar).
- El icono (`app.ico`) se genera con `herramientas/generar_icono.py`.
- `herramientas/probar-arranque.ps1` y `herramientas/analizar-defender.ps1` son
  las comprobaciones que hace GitHub al compilar.
- La ventana del juego se busca por el proceso `nwmain.exe` y, si no, por título
  (sin confundirla con la propia ventana del traductor ni con navegadores).
- Si el juego se ejecuta como administrador, el traductor también tiene que
  ejecutarse como administrador para poder escribir en él.
- El endpoint de Google es público y no oficial: puede bloquear temporalmente si
  recibe muchas peticiones. Microsoft Translator (plan gratuito) es la alternativa
  estable.
