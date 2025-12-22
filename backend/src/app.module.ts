import { Module } from '@nestjs/common';
import { ConfigModule, ConfigService } from '@nestjs/config';
import { TypeOrmModule, TypeOrmModuleOptions } from '@nestjs/typeorm';
import { ScheduleModule } from '@nestjs/schedule';
import { ThrottlerModule } from '@nestjs/throttler';
import { LoggerOptions, LogLevel } from 'typeorm';
import { AppController } from './app.controller';
import { AppService } from './app.service';
import { AdherenceModule } from './adherence/adherence.module';
import { AdminModule } from './admin/admin.module';
import { RedisModule } from './common/redis.module';

@Module({
  imports: [
    RedisModule, // Redis for rate limiting and queues
    AdherenceModule,
    AdminModule,
    // Configuration Module
    ConfigModule.forRoot({
      isGlobal: true,
      envFilePath: ['.env', '.env.local'],
      ignoreEnvFile: false,
    }),

    // Schedule Module (for cron jobs - partition management, cleanup)
    ScheduleModule.forRoot(),

    // TypeORM Configuration
    TypeOrmModule.forRootAsync({
      imports: [ConfigModule],
      useFactory: (configService: ConfigService): TypeOrmModuleOptions => {
        const loggingSetting = process.env.TYPEORM_LOGGING;
        const defaultLogging: LogLevel[] =
          process.env.NODE_ENV === 'development'
            ? ['warn', 'error']
            : ['error'];

        const parseLoggingSetting = (): LoggerOptions => {
          if (!loggingSetting) {
            return defaultLogging;
          }

          if (loggingSetting === 'true') {
            return true;
          }

          if (loggingSetting === 'false' || loggingSetting === '0') {
            return false;
          }

          if (loggingSetting === 'all') {
            return 'all';
          }

          return loggingSetting.split(',') as LogLevel[];
        };

        return {
          type: 'postgres',
          host: configService.get<string>('DATABASE_HOST'),
          port: configService.get<number>('DATABASE_PORT') || 5432,
          username: configService.get<string>('DATABASE_USERNAME'),
          password: configService.get<string>('DATABASE_PASSWORD'),
          database: configService.get<string>('DATABASE_NAME'),
          entities: [__dirname + '/**/*.entity{.ts,.js}'],
          synchronize: false, // Never auto-sync in production - use migrations
          logging: parseLoggingSetting(),
          extra: {
            max: 20, // Maximum number of connections in the pool
            connectionTimeoutMillis: 5000,
          },
        };
      },
      inject: [ConfigService],
    }),

    // Rate Limiting Module
    ThrottlerModule.forRoot([
      {
        ttl: 60000, // 1 minute
        limit: parseInt(process.env.RATE_LIMIT_GLOBAL || '1000', 10),
      },
    ]),
  ],
  controllers: [AppController],
  providers: [AppService],
})
export class AppModule {}

