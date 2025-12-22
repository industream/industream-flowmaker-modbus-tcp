# Frontend build stage
FROM node:20-alpine AS frontend-build
WORKDIR /app/frontend
COPY FlowMaker.ModbusTcp/frontend/package*.json ./
COPY FlowMaker.ModbusTcp/frontend/.npmrc ./
RUN npm install
COPY FlowMaker.ModbusTcp/frontend/ ./
RUN npm run build

# .NET Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy SDK first (for caching)
COPY industream-flowmaker/sdk/dotnet/Industream.FlowMaker.Sdk/ ./industream-flowmaker/sdk/dotnet/Industream.FlowMaker.Sdk/

# Copy project file and restore dependencies
COPY FlowMaker.ModbusTcp/*.csproj ./FlowMaker.ModbusTcp/
WORKDIR /src/FlowMaker.ModbusTcp
RUN dotnet restore

# Copy source files and build
COPY FlowMaker.ModbusTcp/ ./
RUN dotnet publish -c Release -o /app

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Copy published app
COPY --from=build /app .

# Copy UI configuration (js-bundle from frontend build)
COPY --from=frontend-build /app/frontend/dist/config.source.jsbundle.js /usr/app/config/config.source.jsbundle.js

# Environment variables (to be overridden at runtime)
ENV FM_WORKER_ID=""
ENV FM_ROUTER_TRANSPORT_ADDRESS=""
ENV FM_RUNTIME_HTTP_ADDRESS=""
ENV FM_WORKER_LOG_SOCKET_IO_ENDPOINT=""
ENV FM_WORKER_TRANSPORT_ADV_ADDRESS=""
ENV FM_WORKER_APP_CONFIG="/usr/app/config/"
ENV FM_DATACATALOG_URL="http://datacatalog-api:8002"

ENTRYPOINT ["dotnet", "FlowMaker.ModbusTcp.dll"]
