ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:8.0
FROM ${DOTNET_SDK_IMAGE}
RUN apt-get update \
    && apt-get install -y --no-install-recommends libsqlite3-dev \
    && rm -rf /var/lib/apt/lists/*
