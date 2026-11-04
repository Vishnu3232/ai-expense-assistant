# Build: docker build -t ai-expense-assistant .   Run: docker run -p 8080:8080 -e AI__ApiKey=... ai-expense-assistant
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet test tests/ExpenseAssistant.Tests/ExpenseAssistant.Tests.csproj -c Release \
 && dotnet publish src/ExpenseAssistant.Api/ExpenseAssistant.Api.csproj -c Release -o /app --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
# hosts such as Render pass the port in $PORT
CMD ["sh", "-c", "dotnet ExpenseAssistant.Api.dll --urls http://0.0.0.0:${PORT:-8080}"]
