# ── Stage 1: build ───────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project files and restore (layer-cache friendly).
COPY ["src/Company.Observability/Company.Observability.csproj", "src/Company.Observability/"]
COPY ["samples/Company.Observability.SampleApi/Company.Observability.SampleApi.csproj", "samples/Company.Observability.SampleApi/"]
RUN dotnet restore "samples/Company.Observability.SampleApi/Company.Observability.SampleApi.csproj"

# Copy full source and build.
COPY . .
RUN dotnet publish "samples/Company.Observability.SampleApi/Company.Observability.SampleApi.csproj" \
    -c Release \
    -o /app/publish \
    --no-restore

# ── Stage 2: runtime ─────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Non-root user for container security.
RUN addgroup --system appgroup && adduser --system --ingroup appgroup appuser
USER appuser

COPY --from=build /app/publish .

# Expose the ASP.NET Core default port.
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Company.Observability.SampleApi.dll"]
