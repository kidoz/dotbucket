# DotBucket build and management tool

# Build the entire project (frontend and backend)
build: build-frontend build-backend

# Rebuild the entire project from scratch
rebuild: clean build

# Clean build artifacts
clean:
    dotnet clean
    rm -rf src/DotBucket.Server/wwwroot/*
    rm -rf src/DotBucket.UI/dist

# Build the React frontend
build-frontend:
    cd src/DotBucket.UI && npm install && npm run build
    mkdir -p src/DotBucket.Server/wwwroot
    cp -r src/DotBucket.UI/dist/* src/DotBucket.Server/wwwroot/

# Build the .NET backend
build-backend:
    dotnet build

# Create a Docker image
docker:
    docker build -t dotbucket .

# Run the backend locally
run:
    dotnet run --project src/DotBucket.Server

# Format code (C# + frontend)
format:
    dotnet csharpier format .
    cd src/DotBucket.UI && npx prettier --write .

# Check formatting without modifying files
format-check:
    dotnet csharpier check .
    cd src/DotBucket.UI && npx prettier --check .

# Run tests
test:
    dotnet test
