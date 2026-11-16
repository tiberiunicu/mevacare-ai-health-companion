# Build context: the repository root (see deploy/docker-compose.yml).
FROM node:22-alpine AS build
WORKDIR /app
COPY src/frontend/mevacare-ai-web/package.json src/frontend/mevacare-ai-web/package-lock.json ./
RUN npm ci
COPY src/frontend/mevacare-ai-web/ ./
# The Google client id is not baked in: the page asks the API (GET /api/auth/providers), so one build serves every environment.
RUN npm run build

FROM nginx:1.27-alpine AS final
COPY --from=build /app/dist /usr/share/nginx/html
COPY deploy/web.nginx.conf /etc/nginx/conf.d/default.conf
EXPOSE 80
