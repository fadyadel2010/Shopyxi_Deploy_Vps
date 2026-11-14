ARG BASE_IMAGE=docker.io/shopixy/api@sha256:5efeb7042bcaf6ea223f3df06c7983862e073787e78634fb996ffbf9c4cf266e
FROM ${BASE_IMAGE}

ARG API_ASSEMBLY_SHA256
ARG SOURCE_INPUT_SHA256
USER 0:0
RUN rm -rf /usr/share/dotnet/* /app/*
COPY runtime/ /usr/share/dotnet/
RUN ln -sf /usr/share/dotnet/dotnet /usr/bin/dotnet \
    && test "$(dotnet --list-runtimes | grep -c 'Microsoft.AspNetCore.App 10.0.15')" = "1" \
    && test "$(dotnet --list-runtimes | grep -c 'Microsoft.NETCore.App 10.0.15')" = "1"
COPY --chown=1654:1654 app/ /app/
ENV DOTNET_VERSION=10.0.15 \
    ASPNET_VERSION=10.0.15
LABEL org.opencontainers.image.title="Shopixy API automatic subdomains" \
      org.opencontainers.image.source="Shopixy APP82 r11 automatic subdomains independent-audit r3" \
      org.opencontainers.image.revision="${API_ASSEMBLY_SHA256}" \
      shopixy.io/source-input-sha256="${SOURCE_INPUT_SHA256}" \
      shopixy.io/base-image="docker.io/shopixy/api@sha256:5efeb7042bcaf6ea223f3df06c7983862e073787e78634fb996ffbf9c4cf266e" \
      shopixy.io/dotnet-runtime="10.0.15"
WORKDIR /app
USER 1654:1654
ENTRYPOINT ["dotnet", "PointOfSale.API.dll"]
