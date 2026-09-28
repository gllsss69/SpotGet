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

# Встановлюємо FFmpeg (необхідний для конвертації аудіо з YouTube)
RUN apt-get update && apt-get install -y ffmpeg && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "SpotGet.dll"]
