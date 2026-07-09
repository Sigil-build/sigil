# Claude Design brief — Sigil installer wizard

Paste-ready brief for building the wizard in **Claude Design** (claude.ai/design,
or the Claude Desktop sidebar). Work top to bottom: set up the design system
first, then build the screens one prompt at a time (Claude Design responds best
to incremental requests).

Direction (decided): **refined side rail**, **manifest-driven flow**, fully
themeable from the app's brand tokens. Sample brand below = "Acme Studio" on the
Sigil default indigo palette; swap for your real brand when generating.

> **2026-07-09:** a first prototype exists —
> `docs/plan/prototype/sigil-installer-wizard-prototype.html` (Claude Design
> export; its `colors()` maps are the reference constants for spec T7, including
> the `frame` token). It covers welcome / license / options / configure /
> installing / failed / done. **Still to design** (added by the spec revision):
> the Destination screen (T13), the scope toggle (T12), the interactive
> uninstall flow (T15), and the cancel-confirmation state — prompts below. The
> prototype's install-location field must move from Options to Destination.

---

## Step 0 — how to use this

1. Open Claude Design, create a new project named `Sigil installer wizard`.
2. Optionally attach the repo (`/design-sync` from Claude Code) so it can read
   `src/SigilBuild.Installer.Host/` for the existing screens and `BrandTokens`.
3. Paste **Step 1** to establish the design system, then paste each screen
   prompt in order. Refine with inline comments on the canvas.

---

## Step 1 — establish the design system

> Create a design system for a Windows desktop **installer wizard** called
> "Sigil". It must be fully themeable from a small set of brand tokens, because
> each app that ships with Sigil supplies its own colors and logo. Set up these
> tokens and styles:
>
> **Colors (default "indigo" theme — treat as variables, not fixed):**
> - Brand rail: `#312E81` (deep indigo), text on rail `#FFFFFF` / muted
>   `#CECBF6`
> - Accent (primary buttons, active states, progress): `#4F46E5`
> - Primary text `#111827`, secondary `#4B5563`, muted `#6B7280`
> - Surface white `#FFFFFF`, subtle surface `#F9FAFB`, hairline border `#E5E7EB`
> - Success `#0F6E56` on `#E1F5EE`
> Also define a dark-mode variant of each.
>
> **Typography:** system UI sans (Segoe UI / Inter). Scale: title 20/500,
> heading 15/500, body 13.5/400 at 1.6 line-height, label 12/400, mono 11 for
> the install log. Sentence case everywhere, two weights only (400, 500).
>
> **Spacing + shape:** 8px base grid; radius 8px controls, 12px cards, 9px logo
> tile; window corner 12px. Generous padding (20–24px in panes).
>
> **Components to define:** flat brand side-rail (186px), vertical step
> indicator (done / current / upcoming states with a check, a filled dot, and an
> outline dot), primary + ghost buttons, text input with a trailing "Change"
> link, checkbox row with helper subtext, flat progress bar, monospace log
> panel, custom slim title bar (app name left, minimize + close right).
>
> Window frame: 800×500, non-resizable, border-only decoration, centered.
> Keep it flat — no gradients, no drop shadows.

---

## Step 2 — screens (build in order)

Flow is **manifest-driven**: only the screens the app's `sigil.yaml` declares
appear, and the rail's step list is generated from that resolved set. Build the
full set below; note which are conditional.

### Welcome (always)

> Build the Welcome screen. Left rail: logo tile, "Acme Studio", "Acme, Inc.",
> and at the foot "Version 3.2.0 · 84 MB". Step indicator: Welcome (current),
> then Destination, License, Options, Install, Done as upcoming. Pane: title
> "Install Acme Studio", a paragraph explaining it installs v3.2.0 (~84 MB), and
> a **"Signed by Acme, Inc." trust line** with a small shield/lock icon. Footer:
> ghost "Cancel" on the left of a right-aligned primary "Get started".

### Destination (always)

> Build the Destination screen. Destination step current. Pane: title "Choose
> install location"; a path input showing `C:\Users\eugen\AppData\Local\Programs\
> Acme Studio` with a trailing "Browse…" button; beneath it a helper line with
> the required/available disk space. Below, a **scope choice**: two radio rows —
> "Just for me (recommended)" with subtext "No administrator permission needed"
> (selected) and "All users of this computer" with subtext "Installs to Program
> Files — Windows will ask for permission" and a small UAC shield icon.
> Selecting "All users" swaps the path to `C:\Program Files\Acme Studio`.
> Footer: Cancel · Back · primary "Continue". Also show an error state: an
> invalid path renders an inline red message under the input and disables
> Continue.

### License (conditional — only if manifest declares a license)

> Build the License screen. Same rail, License now current. Pane: title "License
> agreement", a scrollable text area with placeholder license text, and a
> checkbox "I accept the terms". The primary "Continue" button is disabled until
> the box is checked. Footer: Cancel · Back · Continue.

### Options (conditional — only if manifest declares options/components)

> Build the Options screen. Options step current. Pane: title "Choose options";
> component checkboxes that map to install steps — "Desktop shortcut" (checked,
> subtext "Add an icon to the desktop"), "Add to PATH" (checked, subtext "Run
> `acme` from any terminal"), "File associations" (unchecked, subtext "Open
> .acme files with Acme Studio"). Include one **locked** row — "Start menu
> entry", checked and disabled, subtext "Always installed" — to cover the
> `locked` component state. No install-location field here (it lives on
> Destination). Footer: Cancel · Back · primary "Install".

### Configure (conditional — one per declared custom screen)

> Build the Configure screen (a manifest-declared parameter form). Configure
> step current. Pane: title "Configure Acme Studio", subtitle "Connect to your
> server and set preferences." Fields, single column: text input "Server
> address" prefilled `https://acme.internal`; **masked secret input** "License
> key" with a show/hide eye toggle; a radio group "Update channel" with
> stable/beta/nightly (stable selected); checkbox "Start when I sign in"
> (checked). Show one field in an invalid state (red border + inline message
> "Enter a valid server address") with Continue disabled. Footer: Cancel · Back
> · primary "Continue".

### Installing (always)

> Build the Installing screen. Install step current. Pane: title "Installing…",
> one line of helper text, a flat progress bar at ~62% with "Extracting payload"
> on the left and "62%" on the right, then a **monospace log panel** showing
> real engine lines: `copy bin/acme.exe → C:\Program Files\Acme Studio`,
> `copy resources/app.asar`, `reg HKLM\Software\Acme\Studio\InstallPath`,
> `path + C:\Program Files\Acme Studio\bin`, `link Desktop\Acme Studio.lnk`.
> Footer: a single ghost "Cancel", right-aligned.

> Also design the **cancel-confirmation state**: clicking Cancel mid-install
> shows an inline confirm ("Stop installing? Changes made so far will be
> undone.") with ghost "Keep installing" and danger "Stop and undo".

### Failed (state of Installing)

> Build the Failed screen. The rail marks the Install step with a red error dot.
> Pane: a red/danger circle with an X, title "Installation failed", one line
> with the failing step's error message, a subtext line "All changes were rolled
> back — your system was left as it was." Keep the log panel visible, last lines
> showing the failure + `rollback` entries. Footer: ghost "View log" · primary
> "Close".

### Uninstall confirm + progress (separate flow — `uninstall.exe`)

> Build the uninstall flow, same window frame and rail (logo, app name, version).
> Screen 1 — confirm: title "Uninstall Acme Studio", body "This removes Acme
> Studio, its Start-menu entry, desktop shortcut, and PATH entry. Your documents
> are not affected." Footer: ghost "Cancel" · danger primary "Uninstall".
> Screen 2 — progress: flat progress bar + the monospace log showing reversal
> lines (`unlink`, `path -`, `reg -`, `delete`). Screen 3 — done: success check,
> "Acme Studio was removed", single "Close" button.

### Done (always)

> Build the Done screen. Done step current (checked). Pane: a green success
> circle with a check, title "Acme Studio is installed", a short "what changed"
> paragraph (Start-menu entry added, `acme` now on PATH), a checked "Launch Acme
> Studio now" checkbox, and a right-aligned primary "Finish". No Back/Cancel.

---

## Step 3 — refine (inline comments)

Good targeted comments once the screens exist:
- "Tighten the rail step spacing to 8px."
- "Make the signed-publisher line green to read as trusted."
- "Show a dark-mode variant of this screen."
- "Give me 2 alternatives for the step indicator."

Ask Claude Design to review contrast/accessibility, and to show a dark-mode pass
— the installer must theme to whatever colors an app supplies.

---

## Mapping back to code

Tokens above correspond to `BrandTokens` in
`src/SigilBuild.Installer.Host/Branding/BrandTokens.cs` (`PrimaryColor`,
`AccentColor`, `LogoFile`, `HeroFile`; gradient stops are removed — the rail is
flat `PrimaryColor`). The derived palette is the prototype's `colors()` maps —
see `prototype/sigil-installer-wizard-prototype.html` and spec T7 (note the
`frame` token). When the design is final, export from Claude Design and hand
off to Claude Code to update `InstallerWindow.axaml` + the `Views/Screens/`
controls. See `docs/plan/IMPLEMENTATION_SPEC.md` for the build sequence.
