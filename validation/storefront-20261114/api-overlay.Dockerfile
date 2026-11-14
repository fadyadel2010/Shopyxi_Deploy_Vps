ARG BASE_IMAGE
FROM ${BASE_IMAGE}
ARG SOURCE_INPUT_SHA256
COPY --chown=1654:1654 PointOfSale.API.dll PointOfSale.API.pdb /app/
LABEL org.opencontainers.image.title="Shopixy Storefront cart-read contention fix 20261114" \
      shopixy.io/source-input-sha256="${SOURCE_INPUT_SHA256}"
