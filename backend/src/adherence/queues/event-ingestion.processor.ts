import { Processor, WorkerHost, OnWorkerEvent } from '@nestjs/bullmq';
import { Logger } from '@nestjs/common';
import { Job } from 'bullmq';
import { EventIngestionService } from '../services/event-ingestion.service';
import { CreateAdherenceEventDto } from '../../dto/create-adherence-event.dto';

const QUEUE_WORKER_CONCURRENCY = parseInt(
  process.env.QUEUE_WORKER_CONCURRENCY ||
    process.env.QUEUE_CONCURRENCY || // support existing env naming in deployment
    '5',
  10,
);

const QUEUE_WORKER_MAX_JOBS = parseInt(
  process.env.QUEUE_WORKER_MAX_JOBS || process.env.QUEUE_MAX_JOBS || '500',
  10,
);

/**
 * EventIngestionProcessor
 * 
 * BullMQ processor for handling queued event ingestion jobs.
 * Processes events asynchronously - Option 2 (Always Queue).
 * 
 * Configuration:
 * - Concurrency: 30 (processes 30 jobs concurrently)
 * - Retry: 3 attempts with exponential backoff
 * - Supports both single events and batch events
 */
@Processor('event-ingestion', {
  concurrency: QUEUE_WORKER_CONCURRENCY,
  limiter: {
    max: QUEUE_WORKER_MAX_JOBS, // Max jobs per duration
    duration: 1000, // Per 1 second
  },
})
export class EventIngestionProcessor extends WorkerHost {
  private readonly logger = new Logger(EventIngestionProcessor.name);

  constructor(private readonly eventIngestionService: EventIngestionService) {
    super();
  }

  /**
   * Process event ingestion job
   * 
   * Handles both single events ('ingest-event') and batch events ('ingest-batch')
   */
  async process(
    job: Job<
      | { workstationId: string; event: CreateAdherenceEventDto }
      | { workstationId: string; events: CreateAdherenceEventDto[] }
    >,
  ) {
    const { workstationId } = job.data;

    // Check if it's a batch job
    if ('events' in job.data && Array.isArray(job.data.events)) {
      return this.processBatch(job as Job<{ workstationId: string; events: CreateAdherenceEventDto[] }>);
    } else if ('event' in job.data) {
      return this.processSingle(job as Job<{ workstationId: string; event: CreateAdherenceEventDto }>);
    } else {
      throw new Error(`Invalid job data format: ${JSON.stringify(job.data)}`);
    }
  }

  /**
   * Process a single event ingestion job
   */
  private async processSingle(job: Job<{ workstationId: string; event: CreateAdherenceEventDto }>) {
    const { workstationId, event } = job.data;

    this.logger.debug(
      `Processing single event job ${job.id} for workstation ${workstationId}`,
    );

    try {
      await this.eventIngestionService.ingestEvent(workstationId, event);
      this.logger.debug(`Successfully processed event job ${job.id}`);
      return { success: true, processed: 1, failed: 0 };
    } catch (error) {
      this.logger.error(
        `Failed to process event job ${job.id}: ${error.message}`,
        error.stack,
      );
      throw error; // BullMQ will retry based on job configuration
    }
  }

  /**
   * Process a batch event ingestion job
   * 
   * Uses bulk insert optimization for better performance.
   */
  private async processBatch(job: Job<{ workstationId: string; events: CreateAdherenceEventDto[] }>) {
    const { workstationId, events } = job.data;

    this.logger.debug(
      `Processing batch job ${job.id} with ${events.length} events for workstation ${workstationId}`,
    );

    try {
      const result = await this.eventIngestionService.ingestBatchEvents(
        workstationId,
        events,
      );
      this.logger.debug(
        `Successfully processed batch job ${job.id}: ${result.processed} processed, ${result.failed} failed`,
      );
      return {
        success: true,
        processed: result.processed,
        failed: result.failed,
      };
    } catch (error) {
      this.logger.error(
        `Failed to process batch job ${job.id}: ${error.message}`,
        error.stack,
      );
      throw error; // BullMQ will retry based on job configuration
    }
  }

  @OnWorkerEvent('completed')
  onCompleted(job: Job) {
    this.logger.debug(`Event ingestion job ${job.id} completed`);
  }

  @OnWorkerEvent('failed')
  onFailed(job: Job, error: Error) {
    this.logger.error(
      `Event ingestion job ${job.id} failed: ${error.message}`,
      error.stack,
    );
  }
}



