# syntax=docker/dockerfile:1
FROM node:22-alpine@sha256:12f54cafa58d0e96554ae05871e29663ff7241836377dc9a7916449b8adb062c AS public
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY . .
RUN npm run build:public && node scripts/package-site.mjs

FROM alpine:3.24@sha256:0f49f331d9212aff11b932863be4eb01cdf962969aa86dab7d1ee21ac6f5f52b
RUN apk add --no-cache nginx=1.30.7-r0 musl=1.2.6-r3 libcrypto3=3.5.9-r1 libssl3=3.5.9-r1 pcre2=10.49-r0 zlib=1.4-r0
ARG SOURCE_COMMIT
LABEL org.opencontainers.image.title="SHOPYXI Marketing" \
      org.opencontainers.image.revision="${SOURCE_COMMIT}"
USER 101:101
COPY --from=public --chown=101:101 /app/.build/container-site/ /site/
COPY --chown=101:101 nginx.conf /etc/nginx/nginx.conf
EXPOSE 8080 8081
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s CMD wget -q -O /dev/null http://127.0.0.1:8081/healthz || exit 1
STOPSIGNAL SIGQUIT
ENTRYPOINT ["/usr/sbin/nginx", "-g", "daemon off;"]
