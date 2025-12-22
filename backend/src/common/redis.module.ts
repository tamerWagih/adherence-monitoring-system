import { Module, Global } from '@nestjs/common';
import { ConfigModule, ConfigService } from '@nestjs/config';
import Redis from 'ioredis';

/**
 * RedisModule
 * 
 * Provides Redis connection for throttler storage and BullMQ queues.
 * Global module - available throughout the application.
 */
@Global()
@Module({
  imports: [ConfigModule],
  providers: [
    {
      provide: 'REDIS_CLIENT',
      useFactory: (configService: ConfigService) => {
        const redisHost = configService.get<string>('REDIS_HOST') || 'localhost';
        const redisPort = configService.get<number>('REDIS_PORT') || 6379;
        const redisPassword = configService.get<string>('REDIS_PASSWORD');
        const redisUrl = configService.get<string>('REDIS_URL');

        // Use REDIS_URL if provided (format: redis://[:password@]host[:port])
        // Otherwise construct from host/port/password
        const connectionOptions: any = redisUrl
          ? redisUrl
          : {
              host: redisHost,
              port: redisPort,
              ...(redisPassword && { password: redisPassword }),
              retryStrategy: (times: number) => {
                const delay = Math.min(times * 50, 2000);
                return delay;
              },
              maxRetriesPerRequest: 3,
              enableReadyCheck: true,
              lazyConnect: true,
            };

        const client = typeof connectionOptions === 'string'
          ? new Redis(connectionOptions, {
              retryStrategy: (times: number) => {
                const delay = Math.min(times * 50, 2000);
                return delay;
              },
              maxRetriesPerRequest: 3,
              enableReadyCheck: true,
              lazyConnect: true,
            })
          : new Redis(connectionOptions);

        client.on('error', (err) => {
          console.error('Redis Client Error:', err);
        });

        client.on('connect', () => {
          console.log('Redis Client Connected');
        });

        return client;
      },
      inject: [ConfigService],
    },
  ],
  exports: ['REDIS_CLIENT'],
})
export class RedisModule {}

