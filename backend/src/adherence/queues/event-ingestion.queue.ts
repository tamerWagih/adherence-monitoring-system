import { Injectable, Inject, Logger } from '@nestjs/common';
import { InjectQueue } from '@nestjs/bullmq';
import { Queue } from 'bullmq';
import { CreateAdherenceEventDto } from '../../dto/create-adherence-event.dto';

/**
 * EventIngestionQueue
 * 
 * Queue service for asynchronous event ingestion.
 * Used during high load periods to queue events for background processing.
 * 
 * Queue Configuration:
 * - Queue Name: 'event-ingestion'
 * - Concurrency: 10 workers (configurable)
 * - Max Jobs: 10000 (configurable)
 * - Job TTL: 1 hour
 * 
 * Usage:
 * - During normal load: Events are processed synchronously
 * - During high load: Events are queued and processed asynchronously
 * - Queue is monitored via health endpoint
 */
@Injectable()
export class EventIngestionQueue {
  private readonly logger = new Logger(EventIngestionQueue.name);

  constructor(
    @InjectQueue('event-ingestion')
    private readonly eventQueue: Queue,
  ) {}

  /**
   * Add event to queue for asynchronous processing
   * 
   * @param workstationId - Workstation ID
   * @param event - Event DTO
   * @returns Job ID
   */
  async queueEvent(
    workstationId: string,
    event: CreateAdherenceEventDto,
  ): Promise<string> {
    try {
      // Add timeout to prevent hanging if Redis is unavailable
      const timeoutPromise = new Promise<never>((_, reject) => {
        setTimeout(() => reject(new Error('Queue operation timeout')), 5000);
      });

      const job = await Promise.race([
        this.eventQueue.add(
          'ingest-event',
          {
            workstationId,
            event,
          },
          {
            attempts: 3,
            backoff: {
              type: 'exponential',
              delay: 2000,
            },
            removeOnComplete: {
              age: 3600, // Keep completed jobs for 1 hour
              count: 5000, // Keep last 5000 completed jobs
            },
            removeOnFail: {
              age: 86400, // Keep failed jobs for 24 hours
            },
          },
        ),
        timeoutPromise,
      ]);

      this.logger.debug(`Queued event job ${job.id} for workstation ${workstationId}`);
      return job.id!;
    } catch (error) {
      this.logger.error(`Failed to queue event: ${error.message}`);
      // Re-throw to let controller handle (will return 500 error)
      throw error;
    }
  }

  /**
   * Add batch of events to queue
   * 
   * Optimized: Queues entire batch as a single job for better performance.
   * Processor will handle batch processing with bulk insert optimization.
   * 
   * @param workstationId - Workstation ID
   * @param events - Array of event DTOs
   * @returns Job ID
   */
  async queueBatchEvents(
    workstationId: string,
    events: CreateAdherenceEventDto[],
  ): Promise<string> {
    if (events.length === 0) {
      throw new Error('Cannot queue empty batch');
    }

    try {
      // Add timeout to prevent hanging if Redis is unavailable
      const timeoutPromise = new Promise<never>((_, reject) => {
        setTimeout(() => reject(new Error('Queue operation timeout')), 5000);
      });

      // Queue entire batch as a single job for better performance
      const job = await Promise.race([
        this.eventQueue.add(
          'ingest-batch',
          {
            workstationId,
            events,
          },
          {
            attempts: 3,
            backoff: {
              type: 'exponential',
              delay: 2000,
            },
            removeOnComplete: {
              age: 3600, // Keep completed jobs for 1 hour
              count: 5000, // Keep last 5000 completed jobs
            },
            removeOnFail: {
              age: 86400, // Keep failed jobs for 24 hours
              count: 10000, // Keep last 10000 failed jobs
            },
          },
        ),
        timeoutPromise,
      ]);

      this.logger.debug(
        `Queued batch job ${job.id} with ${events.length} events for workstation ${workstationId}`,
      );
      return job.id!;
    } catch (error) {
      this.logger.error(`Failed to queue batch events: ${error.message}`);
      // Re-throw to let controller handle (will return 500 error)
      throw error;
    }
  }

  /**
   * Get queue statistics
   */
  async getQueueStats() {
    const [waiting, active, completed, failed, delayed] = await Promise.all([
      this.eventQueue.getWaitingCount(),
      this.eventQueue.getActiveCount(),
      this.eventQueue.getCompletedCount(),
      this.eventQueue.getFailedCount(),
      this.eventQueue.getDelayedCount(),
    ]);

    return {
      waiting,
      active,
      completed,
      failed,
      delayed,
      total: waiting + active + completed + failed + delayed,
    };
  }
}



