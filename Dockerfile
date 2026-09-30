FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Копіюємо файл проєкту і відновлюємо залежності
COPY ["SpotGet.csproj", "./"]
RUN dotnet restore "SpotGet.csproj"

# Копіюємо весь інший код і компілюємо
COPY . .
RUN dotnet publish "SpotGet.csproj" -c Release -o /app/publish

# Створюємо фінальний образ
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

# Встановлюємо FFmpeg, Python3, curl, deno та yt-dlp
RUN apt-get update && \
    apt-get install -y --no-install-recommends ffmpeg python3 curl ca-certificates unzip && \
    curl -fsSL https://deno.land/install.sh | sh -s -- -y && \
    cp /root/.deno/bin/deno /usr/local/bin/deno && \
    curl -L https://github.com/yt-dlp/yt-dlp-nightly-builds/releases/latest/download/yt-dlp -o /usr/local/bin/yt-dlp && \
    chmod a+rx /usr/local/bin/yt-dlp && \
    rm -rf /var/lib/apt/lists/*

# Копіюємо зібраний проєкт
COPY --from=build /app/publish .

# Створюємо необхідні директорії з повними правами на запис для сумісності з mounted volumes
RUN mkdir -p /app/data /tmp/deno-cache /tmp/cache && \
    chmod 777 /app/data /tmp/deno-cache /tmp/cache

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV PYTHONUTF8=1
ENV LANG=C.UTF-8
ENV LC_ALL=C.UTF-8
ENV DENO_DIR=/tmp/deno-cache
ENV XDG_CACHE_HOME=/tmp/cache

# Health Check перевіряє стан сервісу кожні 30 секунд
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD curl -f http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "SpotGet.dll"]
