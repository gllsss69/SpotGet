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

# Встановлюємо FFmpeg, Python3 та yt-dlp (необхідні для завантаження та конвертації аудіо з YouTube)
RUN apt-get update && \
    apt-get install -y --no-install-recommends ffmpeg python3 curl ca-certificates && \
    curl -L https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp -o /usr/local/bin/yt-dlp && \
    chmod a+rx /usr/local/bin/yt-dlp && \
    rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# Створюємо директорію для даних відвідувачів з правами для непривілейованого користувача
RUN mkdir -p /app/data && chmod 777 /app/data

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "SpotGet.dll"]
