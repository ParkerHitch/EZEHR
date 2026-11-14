# Keytos Restyle Plan

Goal: make EZEHR look like a Keytos product by copying the redesigned www.keytos.io. Do it by
re-theming Bootstrap in one stylesheet, not by rewriting page markup.

## Where the design comes from

The redesign lives in one stylesheet on the live site:
`https://www.keytos.io/css/redesign.min.<hash>.css`. It starts with a `:root` block of design
tokens (colours, fonts, type scale, spacing, radii, shadows), followed by media queries that shrink
the type scale on tablet and phone.

**Copy the token blocks verbatim** into `site.css`, and don't download the page HTML. The marketing
markup and its class names (`.kbtn`, `.knav-*`, `.kprose-*`) don't map to our Bootstrap pages.
Only the tokens and a few component rules are useful, and those are restated below. To re-pull
the stylesheet, run this in the browser console on keytos.io:
`fetch([...document.styleSheets].find(s => s.href?.includes('redesign')).href).then(r => r.text())`.

## Design tokens (from keytos.io, Nov 2026)

| Token | Value | Use |
|---|---|---|
| `--color-text-primary` | `#181d23` | body text |
| `--color-text-secondary` | `#56616f` | muted text, secondary button border |
| `--color-text-brand` | `#0071a2` | page titles, hover on links and secondary buttons |
| `--color-btn-primary-default` / `-hover` | `#0071a2` / `#00577c` | primary button |
| `--color-text-button-fill` | `#f7f9fb` | text on primary button |
| `--color-link-rest` | `#00577c` | links |
| `--color-surface-canvas` | `#f7f9fb` | **page background** |
| `--color-surface-card` | `#ffffff` | cards on top of the canvas, nav bar |
| `--color-border-default` | `#dde3ea` | all borders |
| `--color-surface-brand-subtle` | `#dde8f4` | brand-tinted fills |
| `--color-info-default` / `-background` | `#00577c` / `#ebf5ff` | info callout |
| `--color-error-default` / `-background` | `#9e0a17` / `#fee9e9` | error callout |
| `--color-warning-default` / `-background` / `-accent` | `#5c4200` / `#fff8e1` / `#f0b400` | warning callout |
| `--color-success-default` | `#207f4f` | "verified" badges |
| `--color-text-disabled` | `#9aa6b4` | disabled |

Type: headings in **Ubuntu** 700 (`--font-family-primary`), body and buttons in **Inter**
(`--font-family-secondary`), and **DM Mono** for code (`--font-family-mono`, used for the share
code). They load from one Google Fonts URL:
`family=DM+Mono:wght@400;500&family=Inter:wght@400;500;600&family=Ubuntu:wght@400;500;700`.
The scale is `--font-size-h1..h5` and `b1..b4`. Body text is `b3` (16/22.4). Section titles are in
brand blue.

Component rules on the site:

- **Buttons** (`.kbtn`): Inter 500, `b2` size, padding 16px 32px, `--radius-pill`, and a 1px
  border. Primary is filled `#0071a2` (hover `#00577c`). Secondary is transparent, with
  `#181d23` text and a `#56616f` border, both turning brand blue on hover.
- **Nav** (`.knav-root`): sticky, white, 1px bottom border, `--nav-height` 72px (56px on phone).
  Links are Inter 500 `b3`, and turn brand blue on hover or when current.
- **Cards** (`.kcert-card`): white, `--radius-xl` (16px), with a two-layer soft shadow,
  `0 1px 2px rgba(0,0,0,.06), 0 2px 4px rgba(0,0,0,.04)`.
- **Prose** (`.kprose-*`): titles are Ubuntu 700 in brand blue. Links are brand blue, underlined
  with a 2px offset.
- **Footer** (`.kfoot-root`): canvas background, Inter.

## Steps

### 1. Tokens and Bootstrap overrides (`wwwroot/css/site.css`)

- Add the Google Fonts `<link>` in `_Layout.cshtml`.
- Paste the token blocks, with their tablet and phone overrides, at the top of `site.css`.
- Map the tokens onto Bootstrap 5.3 variables: `--bs-body-font-family`, `--bs-body-color`,
  `--bs-body-bg`, `--bs-primary` / `--bs-primary-rgb`, `--bs-link-color`, `--bs-border-color`,
  `--bs-secondary-color`, `--bs-success`, `--bs-danger`.
- Override the component variables, since `.btn-primary` and friends don't read `--bs-primary`:
  - `.btn`: pill radius, Inter 500, and the site's padding (`.btn-sm` stays smaller).
  - `.btn-primary`: filled, as on the site.
  - `.btn-outline-primary` and `.btn-outline-secondary`: the site's secondary button.
  - `.btn-outline-danger`: the same shape in `--color-error-default`.
  - `.alert-*`: a callout with the `--color-*-background` fill, a 4px left bar in
    `--color-*-default` (warning uses `-accent`), no other border, and `--radius-sm`.
  - `.card` and `.list-group`: white on the canvas, `--color-border-default` border,
    `--radius-lg`, and the site's card shadow.
  - `.form-control`: `--radius-md` and a focus ring in brand blue.
  - `.nav-pills .nav-link.active`: brand blue, pill-shaped.
- Replace the stock focus-ring colour `#258cfb` with brand blue.

### 2. Layout shell (`Pages/Shared/_Layout.cshtml` and `.cshtml.css`)

- Delete `_Layout.cshtml.css` (template defaults: `#1b6ec2` buttons, `#0077cc` links,
  box-shadow) and its `EZEHR.styles.css` link, because they fight the theme.
- Header: the site's nav bar, with the **EZEHR wordmark** in Ubuntu 700. Drop the
  Home/Privacy/DB Test nav links, which are scaffolding.
- Footer: canvas background, centered `--color-text-secondary` text in `b4`. Drop the
  `position:absolute` footer hack.
- `main` caps at `--body-max` (800px). Patient pages already cap at 480px, and the 16px gutter
  stays.

### 3. Shared components (small markup changes)

- **Callout**: this is the restyled `.alert-*` from step 1. `_Timeline`'s app notes use
  `alert-info`.
- **Patient nav**: `_PatientNav` marks the current tab `active`, so the pill shows where you are.
- **Landing page** (`Index`): replace the ASP.NET template with a title and two buttons, one for
  patients and one for clinicians.
- Headings: map `h1`–`h5` and `.h1`–`.h5` to Ubuntu 700 and the `--font-size-h*` /
  `--line-height-h*` tokens. Page titles (`h1`) are in brand blue.
- Share code: `.share-code` in `--font-family-mono`.

### 4. Page pass

Work through each page in the browser at phone and desktop widths, fixing spacing only:
`Index` → `Patient/SignIn` → `Enroll` → `Home` (timeline) → `Share` (code and QR) → `Grants`
→ `Activity` → `Clinician/Index` → `Redeem` → `Wait` → `Record` → `AccessEnded`, plus
the full-screen `.approve-prompt`. Keep the 44px tap targets (`.btn-tap`, `.patient-nav`).

### 5. Check

Run the demo story end to end at phone and desktop widths. Check that nothing reads as stock
Bootstrap blue (`#0d6efd`) and that the focus rings are visible.

## Gotcha

`MapRazorPages().WithStaticAssets()` fingerprints `site.css` at build time and serves it with the
build-time length. After you edit the CSS, **restart the server**, or the browser gets a truncated
stylesheet.

## Out of scope

- Dark mode, since the site is light-only.
- Downloading keytos.io page HTML. Only its tokens are useful.
