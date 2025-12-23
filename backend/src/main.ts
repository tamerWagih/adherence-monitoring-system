import { NestFactory } from '@nestjs/core';
import { ValidationPipe } from '@nestjs/common';
import { SwaggerModule, DocumentBuilder } from '@nestjs/swagger';
import { AppModule } from './app.module';

async function bootstrap() {
  const app = await NestFactory.create(AppModule);

  // Global validation pipe
  app.useGlobalPipes(
    new ValidationPipe({
      whitelist: true,
      forbidNonWhitelisted: true,
      transform: true,
      transformOptions: {
        enableImplicitConversion: true,
      },
      skipMissingProperties: false,
      skipNullProperties: false,
      skipUndefinedProperties: true,
    }),
  );

  // Set global API prefix
  app.setGlobalPrefix('api/adherence');

  // Swagger / OpenAPI
  const swaggerConfig = new DocumentBuilder()
    .setTitle('Adherence Monitoring Backend API')
    .setDescription('API documentation for the Adherence Monitoring System')
    .setVersion('1.0.0')
    // JWT for System_Admin endpoints
    .addBearerAuth(
      {
        type: 'http',
        scheme: 'bearer',
        bearerFormat: 'JWT',
        name: 'Authorization',
        description: 'Paste JWT access token (without "Bearer ")',
        in: 'header',
      },
      'JWT-auth',
    )
    // Desktop Agent auth (workstation headers)
    .addApiKey(
      {
        type: 'apiKey',
        name: 'X-API-Key',
        in: 'header',
        description: 'Workstation API key (Desktop Agent)',
      },
      'API-Key',
    )
    .addApiKey(
      {
        type: 'apiKey',
        name: 'X-Workstation-ID',
        in: 'header',
        description: 'Workstation ID (Desktop Agent)',
      },
      'Workstation-ID',
    )
    .addTag('Authentication')
    .addTag('Summaries')
    .addTag('Reports')
    .addTag('Admin')
    .addTag('Events')
    .addTag('Agent')
    .build();

  const swaggerDocument = SwaggerModule.createDocument(app, swaggerConfig);
  // Note: SwaggerModule.setup path is NOT affected by app.setGlobalPrefix
  // Swagger UI will be available at: http://host:port/api/docs
  const swaggerSetupOptions = {
    swaggerOptions: {
      persistAuthorization: true,
    },
    customSiteTitle: 'Adherence Backend API Docs',
  };

  // Local/dev convenience (direct backend access)
  SwaggerModule.setup('api/docs', app, swaggerDocument, swaggerSetupOptions);
  // VM / reverse-proxy compatibility (most setups only proxy /api/adherence/*)
  SwaggerModule.setup(
    'api/adherence/docs',
    app,
    swaggerDocument,
    swaggerSetupOptions,
  );

  // Add health endpoint (before prefix)
  app.getHttpAdapter().get('/health', (req, res) => {
    res.status(200).json({
      status: 'ok',
      timestamp: new Date().toISOString(),
      service: 'Adherence Monitoring Backend',
      version: '1.0.0',
    });
  });

  // Enable CORS
  const nodeEnv = process.env.NODE_ENV || 'development';
  const isDev = nodeEnv === 'development' || nodeEnv === 'staging';

  if (isDev) {
    // Development/Staging: Allow all origins
    app.enableCors({
      origin: true,
      credentials: true,
    });
    console.log('🔓 CORS: Allowing all origins (development/staging mode)');
  } else {
    // Production: Strict origin validation
    const corsOrigin = process.env.CORS_ORIGIN || 'http://localhost';
    const corsOrigins = [
      corsOrigin,
      corsOrigin.replace(':3001', ''),
      corsOrigin.replace(':80', ''),
      'http://localhost:3001',
      'http://localhost',
    ].filter((origin, index, self) => self.indexOf(origin) === index);

    app.enableCors({
      origin: (origin, callback) => {
        if (!origin) return callback(null, true);

        const isAllowed = corsOrigins.some((allowed) => {
          if (origin === allowed) return true;

          try {
            const allowedUrl = new URL(allowed);
            const originUrl = new URL(origin);
            return (
              allowedUrl.protocol === originUrl.protocol &&
              allowedUrl.hostname === originUrl.hostname
            );
          } catch {
            return false;
          }
        });

        if (isAllowed) {
          callback(null, true);
        } else {
          console.warn(`[CORS] Rejected origin: ${origin}`);
          callback(new Error('Not allowed by CORS'));
        }
      },
      credentials: true,
    });
    console.log('🔒 CORS: Strict origin validation enabled (production mode)');
  }

  const port = process.env.PORT || process.env.BACKEND_PORT || 4001;
  await app.listen(port);
  console.log(`🚀 Adherence Backend server running on port ${port}`);
  console.log(`📊 Health check: http://localhost:${port}/health`);
  console.log(`🔌 API endpoints: http://localhost:${port}/api/adherence`);
  console.log(`📚 Swagger UI: http://localhost:${port}/api/docs`);
}

bootstrap();

