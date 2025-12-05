# Backend Build Test Commands

## On VM (Linux)

### 1. Navigate to Backend Directory
```bash
cd adherence-monitoring-system/backend
```

### 2. Install Dependencies
```bash
npm install
```

**Note:** This may take a few minutes as it installs all NestJS dependencies.

### 3. Build the Project
```bash
npm run build
```

**Expected Output:**
- Should compile TypeScript to JavaScript
- Should create `dist/` directory
- Should show "Build completed successfully" or similar

### 4. (Optional) Check Build Output
```bash
ls -la dist/
# Should see: main.js, app.module.js, app.controller.js, app.service.js
```

### 5. (Optional) Try to Start (Will Fail on DB Connection - This is Expected)
```bash
npm run start:prod
```

**Expected Behavior:**
- May fail with database connection error (this is OK - we haven't set up entities yet)
- Or may start but fail when trying to connect to DB
- This is expected until we configure entities in Week 5

### 6. Check for Errors
```bash
# If build fails, check for:
# - Missing dependencies (run npm install again)
# - TypeScript errors (check tsconfig.json)
# - Missing files (verify all src files exist)
```

## Troubleshooting

### If npm install fails:
```bash
# Clear cache and retry
npm cache clean --force
rm -rf node_modules package-lock.json
npm install
```

### If build fails with TypeScript errors:
```bash
# Check TypeScript version
npx tsc --version

# Try building with verbose output
npm run build -- --verbose
```

### If you see "Cannot find module" errors:
```bash
# Make sure all dependencies are installed
npm install --legacy-peer-deps
```

