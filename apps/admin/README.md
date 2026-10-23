# @biatec/admin

Administrator dashboard (Vue 3 + Vite + TypeScript). Spec and HTTP contract: [docs/admin-dashboard.md](../../docs/admin-dashboard.md).
The backend serves the build at `/admin/` (Vite production `base` is `./`, API URLs are document-relative (`api/me`) so a path prefix mount works), with a strict CSP (`script-src 'self'; style-src 'self'`): no inline
scripts or styles, no CDN, transcripts rendered as text only.

```bash
pnpm --filter @biatec/admin dev        # http://localhost:5174/admin/ ; proxies /admin/api and /admin/auth
pnpm --filter @biatec/admin test
pnpm --filter @biatec/admin build      # vue-tsc + vite build -> apps/admin/dist
```

`VITE_BACKEND_URL` (default `http://localhost:5000`) is the backend the dev server proxies to. Routes are hash based
(`#/`, `#/users`, `#/user?tenant=&sub=`, `#/view`). Only a page-size preference is kept in `localStorage`.
