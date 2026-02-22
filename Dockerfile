# Stage 1: Build the React Frontend
FROM node:24-alpine AS frontend-build
WORKDIR /app/frontend

# Copy frontend package files and install dependencies
COPY src/DotBucket.UI/package*.json ./
RUN npm ci

# Copy the rest of the frontend source and build it
COPY src/DotBucket.UI/ ./
RUN npm run build

# Stage 2: Build the .NET Backend (Native AOT)
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS backend-build
WORKDIR /app/backend

# Install build dependencies for Native AOT on Alpine
RUN apk add --no-cache clang build-base zlib-dev

# Copy the project file and restore dependencies
COPY src/DotBucket.Server/DotBucket.Server.csproj src/DotBucket.Server/
RUN dotnet restore src/DotBucket.Server/DotBucket.Server.csproj

# Copy the remaining backend source code
COPY src/DotBucket.Server/ src/DotBucket.Server/

# Copy the frontend build artifacts into the backend's wwwroot directory
# (Must be done before publish so they are included in the AOT bundle if needed, 
# although for web apps they are usually just copied to the output folder)
COPY --from=frontend-build /app/frontend/dist /app/backend/src/DotBucket.Server/wwwroot

# Publish the .NET application as Native AOT
WORKDIR /app/backend/src/DotBucket.Server
RUN dotnet publish -c Release -o /app/publish

# Stage 3: Create the final runtime image
# For Native AOT, we only need runtime-deps as the binary is self-contained
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-alpine AS final
WORKDIR /app

# Install curl for healthchecks
RUN apk add --no-cache curl

# Expose port 9000 (MinIO's default API/UI port)
EXPOSE 9000
ENV ASPNETCORE_HTTP_PORTS=9000

# Create a default storage directory for the container
RUN mkdir -p /data && chown -R 1000:1000 /data
ENV Storage__RootPath=/data

# Switch to a non-root user for security
USER 1000

# Copy the published self-contained native binary and assets
COPY --from=backend-build /app/publish .

# The entrypoint is now the native executable name
ENTRYPOINT ["/app/DotBucket.Server"]