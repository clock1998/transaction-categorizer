# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src

COPY ["TransactionCategorizer.csproj", "."]
RUN dotnet restore "TransactionCategorizer.csproj"

COPY . .
RUN dotnet build "TransactionCategorizer.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "TransactionCategorizer.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview AS final
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

EXPOSE 8080

COPY --from=publish /app/publish .

USER $APP_UID

ENTRYPOINT ["dotnet", "TransactionCategorizer.dll"]
