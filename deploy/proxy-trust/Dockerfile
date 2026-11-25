# Independent normalization runtime: no API assembly or frontend source is rebuilt.
# Frontend static assets must come from the EXACT approved predecessor image.
FROM ubuntu:24.04@sha256:9a04f13b3f80ef056361e30868593b1cfd2279fe19e4081cb93a90bf56819d6a
RUN apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
    nginx=1.24.0-2ubuntu7.21 \
    libnginx-mod-http-js=0.8.2-1ubuntu1 \
    libnginx-mod-http-brotli-filter=1.0.0~rc-5build1 \
    libnginx-mod-http-brotli-static=1.0.0~rc-5build1 \
    python3 ca-certificates \
    && rm -rf /var/lib/apt/lists/*
COPY normalizer.mjs /etc/proxy-trust/normalizer.mjs
COPY reconciler.py consumer.py /opt/proxy-trust/
RUN mkdir -p /run/proxy-trust /usr/share/nginx/html && chmod 755 /run/proxy-trust
USER 65532:65532
ENTRYPOINT ["/usr/sbin/nginx", "-g", "daemon off;"]
