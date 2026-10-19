# Build context is the repository root: docker build -f backend/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY backend/Biatec.Chat.Core/Biatec.Chat.Core.csproj backend/Biatec.Chat.Core/
COPY backend/Biatec.Chat.Tools/Biatec.Chat.Tools.csproj backend/Biatec.Chat.Tools/
COPY backend/Biatec.Chat.Api/Biatec.Chat.Api.csproj backend/Biatec.Chat.Api/
RUN dotnet restore backend/Biatec.Chat.Api/Biatec.Chat.Api.csproj
COPY backend/Biatec.Chat.Core backend/Biatec.Chat.Core
COPY backend/Biatec.Chat.Tools backend/Biatec.Chat.Tools
COPY backend/Biatec.Chat.Api backend/Biatec.Chat.Api
RUN dotnet publish backend/Biatec.Chat.Api/Biatec.Chat.Api.csproj -c Release --no-restore -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ENV ASPNETCORE_URLS=http://+:8080
WORKDIR /app
COPY --from=build /out .
# The aspnet image ships a non-root "app" user (UID 1654); numeric so the kubelet can verify runAsNonRoot.
USER 1654
EXPOSE 8080
CMD ["dotnet", "Biatec.Chat.Api.dll"]
