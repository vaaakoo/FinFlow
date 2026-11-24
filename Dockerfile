FROM mcr.microsoft.com/dotnet/sdk:10.0 AS sdk
WORKDIR /source
COPY Directory.Build.props Directory.Packages.props ./
COPY src/ src/

FROM sdk AS ledger-build
RUN dotnet publish src/FinFlow.Ledger.Api -c Release -o /app /p:UseAppHost=false

FROM sdk AS worker-build
RUN dotnet publish src/FinFlow.Notifications.Worker -c Release -o /app /p:UseAppHost=false

FROM sdk AS gateway-build
RUN dotnet publish src/FinFlow.Gateway -c Release -o /app /p:UseAppHost=false

FROM sdk AS customers-build
RUN dotnet publish src/FinFlow.Customers.Api -c Release -o /app /p:UseAppHost=false

FROM sdk AS transfers-build
RUN dotnet publish src/FinFlow.Transfers.Api -c Release -o /app /p:UseAppHost=false

FROM sdk AS audit-build
RUN dotnet publish src/FinFlow.Audit.Api -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS ledger
WORKDIR /app
COPY --from=ledger-build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "FinFlow.Ledger.Api.dll"]

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS worker
WORKDIR /app
COPY --from=worker-build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "FinFlow.Notifications.Worker.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS gateway
WORKDIR /app
COPY --from=gateway-build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "FinFlow.Gateway.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS customers
WORKDIR /app
COPY --from=customers-build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "FinFlow.Customers.Api.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS transfers
WORKDIR /app
COPY --from=transfers-build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "FinFlow.Transfers.Api.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS audit
WORKDIR /app
COPY --from=audit-build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "FinFlow.Audit.Api.dll"]

