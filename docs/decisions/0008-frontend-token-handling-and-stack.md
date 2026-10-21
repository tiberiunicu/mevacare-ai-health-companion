# ADR-0008: SPA stack and in-memory access token

**Status:** Accepted

**Decision.** React + TypeScript + Vite + Tailwind CSS 4, React Router, TanStack Query (server state), Zustand (session
and theme only), react-i18next, Vitest + Testing Library. The access token is kept in a Zustand store (memory only);
the httpOnly refresh cookie restores it on reload through a single-flight refresh call. The Vite dev server proxies
`/api` so the SameSite=Strict cookie is same-origin in development and in a reverse-proxied production setup. Chat
streaming uses `fetch` with a `ReadableStream` (not `EventSource`, which cannot send an Authorization header or a POST
body) and a small tested SSE parser.

**Consequences.** XSS cannot read a long-lived credential, at the price of a refresh round trip on each page load.
Hosting the SPA and the API on different sites would require changing the cookie policy. Assistant output is rendered
with `react-markdown`, which does not render raw HTML.
