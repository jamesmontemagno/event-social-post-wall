---
name: "React UI and accessibility instructions"
description: "Use when changing the EventWall React, TypeScript, CSS, or HTML frontend, including public/admin experiences and accessibility."
applyTo: "src/EventWall.Web/**"
---

# React UI and accessibility

- Preserve the separate public/admin builds selected by `VITE_SITE_ROLE` in [`main.tsx`](../../src/EventWall.Web/src/main.tsx). Keep staff-only UI out of the public bundle, use the matching same-origin API, and do not treat a query parameter or client-side check as a security boundary.
- Follow the existing TypeScript style, including type-only imports, and colocate component tests as `*.test.tsx` using Vitest and Testing Library. Prefer queries by accessible role and name, and test keyboard-relevant state and interactions.
- Use semantic HTML and native controls. Give icon-only controls accessible names, expose toggle state with appropriate ARIA attributes, and announce dynamic status/errors with suitable live-region semantics.
- Keep all interactions keyboard-operable with a visible focus indicator. For modal flows, follow the native `<dialog>` pattern in [`ConfirmationDialog.tsx`](../../src/EventWall.Web/src/ConfirmationDialog.tsx), including an accessible title/description and a usable cancel path.
- Check text, controls, focus indicators, and status meaning in both light and dark themes. Use the CSS variables in [`index.css`](../../src/EventWall.Web/src/index.css), maintain readable contrast without relying on color alone, and preserve the existing touch-friendly control sizing.
- Respect `prefers-reduced-motion` when adding animation or transitions; the shared stylesheet already provides a reduced-motion rule.
- Provide useful image alt text or a meaningful fallback, and preserve the existing unavailable-image state and `no-referrer` behavior for remote post media.
- Verify responsive layouts and accessibility in the relevant public and staff views. See [the shared instructions](../copilot-instructions.md) for verified frontend test, build, and lint commands.
