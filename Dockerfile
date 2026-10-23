# WeldTrace 실행 이미지 (수집기·시뮬레이터·웹 공용)
#   docker build --build-arg PROJECT=WeldTrace.Collector -t weldtrace-collector .
#   웹은 ASP.NET 런타임이 필요: --build-arg BASE=mcr.microsoft.com/dotnet/aspnet:8.0
ARG BASE=mcr.microsoft.com/dotnet/runtime:8.0

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG PROJECT
WORKDIR /src
# 프로젝트 파일만 먼저 복사해 패키지 복원 레이어를 캐시한다
COPY Directory.Build.props ./
COPY src/WeldTrace.Core/WeldTrace.Core.csproj src/WeldTrace.Core/
COPY src/WeldTrace.Data/WeldTrace.Data.csproj src/WeldTrace.Data/
COPY src/${PROJECT}/${PROJECT}.csproj src/${PROJECT}/
RUN dotnet restore src/${PROJECT}/${PROJECT}.csproj
COPY src/ src/
RUN dotnet publish src/${PROJECT}/${PROJECT}.csproj -c Release -o /app --no-restore

FROM ${BASE}
ARG PROJECT
ENV APP_DLL=${PROJECT}.dll
WORKDIR /app
COPY --from=build /app .
COPY data/sample/ /app/sample/
ENTRYPOINT ["sh", "-c", "exec dotnet \"$APP_DLL\" \"$@\"", "--"]
