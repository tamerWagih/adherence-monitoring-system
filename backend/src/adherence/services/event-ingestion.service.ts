import { Injectable } from '@nestjs/common';
import { InjectRepository } from '@nestjs/typeorm';
import { Repository } from 'typeorm';
import { AgentAdherenceEvent } from '../../entities/agent-adherence-event.entity';
import { CreateAdherenceEventDto } from '../../dto/create-adherence-event.dto';
import { randomUUID } from 'crypto';

/**
 * EventIngestionService
 * 
 * Handles ingestion of events from Desktop Agents.
 * Stores events in the partitioned agent_adherence_events table.
 */
@Injectable()
export class EventIngestionService {
  constructor(
    @InjectRepository(AgentAdherenceEvent)
    private eventRepo: Repository<AgentAdherenceEvent>,
  ) {}

  /**
   * Ingest a single event
   */
  async ingestEvent(
    employeeId: string,
    workstationId: string,
    eventDto: CreateAdherenceEventDto,
  ): Promise<AgentAdherenceEvent> {
    const event = this.eventRepo.create({
      id: randomUUID(),
      employeeId,
      workstationId,
      eventType: eventDto.event_type,
      eventTimestamp: new Date(eventDto.event_timestamp),
      applicationName: eventDto.application_name,
      applicationPath: eventDto.application_path,
      windowTitle: eventDto.window_title,
      isWorkApplication: eventDto.is_work_application,
      metadata: eventDto.metadata || {},
    });

    return this.eventRepo.save(event);
  }

  /**
   * Ingest multiple events in batch
   */
  async ingestBatchEvents(
    employeeId: string,
    workstationId: string,
    events: CreateAdherenceEventDto[],
  ): Promise<{ processed: number; failed: number }> {
    let processed = 0;
    let failed = 0;

    for (const eventDto of events) {
      try {
        await this.ingestEvent(employeeId, workstationId, eventDto);
        processed++;
      } catch (error) {
        failed++;
        // Log error but continue processing other events
        console.error(`Failed to ingest event: ${error.message}`);
      }
    }

    return { processed, failed };
  }
}

