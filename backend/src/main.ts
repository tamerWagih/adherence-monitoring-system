import { NestFactory } from '@nestjs/core';
import { ValidationPipe } from '@nestjs/common';
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
}

bootstrap();

