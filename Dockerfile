FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
ARG DEBIAN_FRONTEND=noninteractive
RUN apt update -y && apt install -y fontconfig libc6-dev
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /solution
COPY . .
#RUN bash versionUpdate.sh
WORKDIR /solution/src/Web
RUN dotnet restore "Web.csproj" -v=q
ARG TARGETARCH
ARG TARGETPLATFORM
RUN dotnet publish "/solution/src/Web/Web.csproj" --output /app  --configuration Release

FROM base AS final
WORKDIR /app
COPY --from=build /app .
ENV TZ=Asia/Bangkok
RUN ln -snf /usr/share/zoneinfo/$TZ /etc/localtime && echo $TZ > /etc/timezone
ENTRYPOINT ["dotnet", "ChatbotApi.Web.dll"]
