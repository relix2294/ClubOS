# ClubOS Admin Web (Next.js 16, standalone). Контекст сборки — корень репозитория.
FROM node:22-alpine AS build
WORKDIR /repo
COPY packages/contracts-typescript/ packages/contracts-typescript/
COPY apps/admin-web/package.json apps/admin-web/package-lock.json apps/admin-web/
RUN cd apps/admin-web && npm ci --no-audit --no-fund
COPY apps/admin-web/ apps/admin-web/
ENV NEXT_TELEMETRY_DISABLED=1
RUN cd apps/admin-web && npm run build

FROM node:22-alpine
WORKDIR /app
ENV NODE_ENV=production NEXT_TELEMETRY_DISABLED=1 PORT=3000 HOSTNAME=0.0.0.0
COPY --from=build --chown=node:node /repo/apps/admin-web/.next/standalone ./
COPY --from=build --chown=node:node /repo/apps/admin-web/.next/static ./apps/admin-web/.next/static
USER node
EXPOSE 3000
CMD ["node", "apps/admin-web/server.js"]
