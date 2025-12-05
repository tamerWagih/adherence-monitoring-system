# Backend Build Test Commands

## Using Docker (Recommended)

### 1. Navigate to Repository Root
```bash
cd adherence-monitoring-system
```

### 2. Build Backend Docker Image
```bash
docker-compose build backend
```

**Expected Output:**
- Downloads Node.js base image
- Installs dependencies
- Compiles TypeScript
- Creates Docker image

### 3. Check if Image was Created
```bash
docker images | grep adherence-backend
```

### 4. (Optional) Test Run Container (Will fail on DB connection - Expected)
```bash
docker-compose up backend
```

**Expected Behavior:**
- Container starts
- May fail with database connection error (this is OK - we haven't set up entities yet)
- Or may show connection errors when trying to connect to PostgreSQL
- This is expected until we configure TypeORM entities in Week 5

### 5. Check Container Logs
```bash
docker-compose logs backend
```

### 6. Stop Container
```bash
docker-compose down
```

## Alternative: Build All Services
```bash
# Build all services (backend, frontend, nginx)
docker-compose build

# Or build without cache (if you need fresh build)
docker-compose build --no-cache backend
```

## Troubleshooting

### If Docker build fails:
```bash
# Check Docker is running
docker ps

# Check docker-compose version
docker-compose --version

# Build with verbose output
docker-compose build --progress=plain backend
```

### If you see "Cannot find module" errors:
- Make sure `backend/package.json` exists
- Check that all dependencies are listed in package.json
- Try building with `--no-cache` flag

### If build succeeds but container fails to start:
- Check `.env` file exists in repository root
- Verify database connection settings in `.env`
- Check logs: `docker-compose logs backend`

## Quick Test (All in One)
```bash
cd adherence-monitoring-system && \
docker-compose build backend && \
echo "✅ Docker build completed! Check with: docker images | grep adherence"
```
