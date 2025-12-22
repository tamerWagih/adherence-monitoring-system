import { Processor, WorkerHost, OnWorkerEvent } from '@nestjs/bullmq';
import { Logger } from '@nestjs/common';
import { Job } from 'bullmq';
import { EventIngestionService } from '../services/event-ingestion.service';
import { CreateAdherenceEventDto } from '../../dto/create-adherence-event.dto';

/**
 * EventIngestionProcessor
 * 
 * BullMQ processor for handling queued event ingestion jobs.
 * Processes events asynchronously during high load periods.
 * 
 * Configuration:
 * - Concurrency: 10 (processes 10 jobs concurrently)
 * - Retry: 3 attempts with exponential backoff
 */
@Processor('event-ingestion', {
  concurrency: 10, // Process 10 jobs concurrently
})
export class EventIngestionProcessor extends WorkerHost {
  private readonly logger = new Logger(EventIngestionProcessor.name);

  constructor(private readonly eventIngestionService: EventIngestionService) {
    super();
  }

  /**
   * Process a single event ingestion job
   */
  async process(job: Job<{ workstationId: string; event: CreateAdherenceEventDto }>) {
    const { workstationId, event } = job.data;

    this.logger.debug(
      `Processing event ingestion job ${job.id} for workstation ${workstationId}`,
    );

    try {
      await this.eventIngestionService.ingestEvent(workstationId, event);
      this.logger.debug(`Successfully processed event job ${job.id}`);
      return { success: true };
    } catch (error) {
      this.logger.error(
        `Failed to process event job ${job.id}: ${error.message}`,
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



