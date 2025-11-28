# Build stage
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

# Copy UI configuration
COPY FlowMaker.ModbusTcp/config/config.uimaker.json /usr/app/config/config.uimaker.json

# Environment variables (to be overridden at runtime)
ENV FM_WORKER_ID=""
ENV FM_ROUTER_TRANSPORT_ADDRESS=""
ENV FM_RUNTIME_HTTP_ADDRESS=""
ENV FM_WORKER_LOG_SOCKET_IO_ENDPOINT=""
ENV FM_WORKER_TRANSPORT_ADV_ADDRESS=""
ENV FM_WORKER_APP_CONFIG="/usr/app/config/"

ENTRYPOINT ["dotnet", "FlowMaker.ModbusTcp.dll"]
