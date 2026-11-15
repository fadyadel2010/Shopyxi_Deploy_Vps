ARG BASE_IMAGE
FROM ${BASE_IMAGE}
ARG SOURCE_INPUT_SHA256
COPY --chown=1654:1654 PointOfSale.API.dll PointOfSale.API.pdb PointOfSale.Core.dll PointOfSale.Core.pdb PointOfSale.Infrastructure.dll PointOfSale.Infrastructure.pdb /app/
LABEL org.opencontainers.image.title="Shopixy audited manual Affiliate payout return and durable QA communication exclusion" \
      shopixy.io/source-input-sha256="${SOURCE_INPUT_SHA256}"
