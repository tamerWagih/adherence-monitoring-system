# Frontend Build Test Commands

## Using Docker (Recommended)

### 1. Navigate to Repository Root
```bash
cd adherence-monitoring-system
```

### 2. Build Frontend Docker Image
```bash
docker compose build frontend
```

**Expected Output:**
- Downloads Node.js base image
- Installs npm dependencies
- Builds Next.js application
- Creates Docker image

### 3. Check if Image was Created
```bash
docker images | grep adherence-frontend
```

### 4. Test Run Container
```bash
docker compose up frontend
```

**Expected Behavior:**
- Container starts
- Next.js server runs on port 3001
- Should show "Ready" message

### 5. Check Container Logs
```bash
docker compose logs frontend
```

### 6. Stop Container
```bash
docker compose down
```

## Test Full Stack (Backend + Frontend + NGINX)

### 1. Build All Services
```bash
docker compose build
```

### 2. Start All Services
```bash
docker compose up -d
```

### 3. Check All Services are Running
```bash
docker compose ps
```

### 4. Test Endpoints
```bash
# Backend health
curl http://localhost/api/adherence/health

# Frontend health
curl http://localhost/api/health

# Frontend page (via NGINX)
curl http://localhost/
```

### 5. View Logs
```bash
# All services
docker compose logs -f

# Specific service
docker compose logs -f frontend
docker compose logs -f backend
docker compose logs -f nginx
```

### 6. Stop All Services
```bash
docker compose down
```

## Troubleshooting

### If Docker build fails:
```bash
# Check Docker is running
docker ps

# Build with verbose output
docker compose build --progress=plain frontend

# Build without cache (fresh build)
docker compose build --no-cache frontend
```

### If frontend fails to start:
- Check `.env` file exists in repository root
- Verify `NEXT_PUBLIC_API_URL` is set correctly
- Check logs: `docker compose logs frontend`

### If NGINX can't connect to frontend:
- Verify frontend container is running: `docker compose ps`
- Check network: `docker network ls`
- Verify NGINX config: `cat nginx/nginx.conf`

## Quick Test (All in One)
```bash
cd adherence-monitoring-system && \
docker compose build frontend && \
echo "✅ Frontend Docker build completed!"
```

