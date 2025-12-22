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
    const job = await this.eventQueue.add(
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
          count: 1000, // Keep last 1000 completed jobs
        },
        removeOnFail: {
          age: 86400, // Keep failed jobs for 24 hours
        },
      },
    );

    this.logger.debug(`Queued event job ${job.id} for workstation ${workstationId}`);
    return job.id!;
  }

  /**
   * Add batch of events to queue
   * 
   * @param workstationId - Workstation ID
   * @param events - Array of event DTOs
   * @returns Array of job IDs
   */
  async queueBatchEvents(
    workstationId: string,
    events: CreateAdherenceEventDto[],
  ): Promise<string[]> {
    const jobs = await Promise.all(
      events.map((event) =>
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
              age: 3600,
              count: 1000,
            },
            removeOnFail: {
              age: 86400,
            },
          },
        ),
      ),
    );

    const jobIds = jobs.map((job) => job.id!).filter(Boolean) as string[];
    this.logger.debug(
      `Queued ${jobIds.length} event jobs for workstation ${workstationId}`,
    );
    return jobIds;
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



