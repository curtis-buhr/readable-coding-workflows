FROM node:22-bookworm-slim AS node-runtime
FROM mcr.microsoft.com/dotnet/sdk:10.0.403-noble

COPY --from=node-runtime /usr/local/bin/node /usr/local/bin/node
RUN node --version && dotnet --version
