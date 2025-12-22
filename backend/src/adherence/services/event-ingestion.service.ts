import { Injectable, ConflictException, BadRequestException, Logger } from '@nestjs/common';
import { InjectRepository } from '@nestjs/typeorm';
import { Repository } from 'typeorm';
import { AgentAdherenceEvent } from '../../entities/agent-adherence-event.entity';
import { EmployeePersonalInfo } from '../../entities/employee-personal-info.entity';
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
  private readonly logger = new Logger(EventIngestionService.name);

  constructor(
    @InjectRepository(AgentAdherenceEvent)
    private eventRepo: Repository<AgentAdherenceEvent>,
    @InjectRepository(EmployeePersonalInfo)
    private employeePersonalInfoRepo: Repository<EmployeePersonalInfo>,
  ) {}

  /**
   * Resolve employee_id from NT account
   */
  private async resolveEmployeeIdFromNt(nt: string): Promise<string> {
    const personalInfo = await this.employeePersonalInfoRepo.findOne({
      where: { nt },
    });

    if (!personalInfo || !personalInfo.employeeId) {
      throw new ConflictException(
        `Employee not found for NT account: ${nt}. Please ensure the NT account is registered in employee_personal_info.`,
      );
    }

    return personalInfo.employeeId;
  }

  /**
   * Ingest a single event
   * 
   * Resolves employee_id from NT account at ingestion time.
   * Throws 409 Conflict if NT account is not mapped.
   */
  async ingestEvent(
    workstationId: string,
    eventDto: CreateAdherenceEventDto,
  ): Promise<AgentAdherenceEvent> {
    // Validate NT is present
    if (!eventDto.nt) {
      throw new BadRequestException('nt field is required (Windows NT account sam_account_name)');
    }

    // Support both 'timestamp' and 'event_timestamp' field names
    const timestamp = eventDto.event_timestamp || eventDto.timestamp;
    
    if (!timestamp) {
      throw new BadRequestException('event_timestamp or timestamp is required');
    }

    const eventDate = new Date(timestamp);
    if (isNaN(eventDate.getTime())) {
      throw new BadRequestException(`Invalid timestamp format: ${timestamp}. Expected ISO 8601 format (e.g., 2025-12-05T10:30:00Z)`);
    }

    // Resolve employee_id from NT account
    const employeeId = await this.resolveEmployeeIdFromNt(eventDto.nt);

    const event = this.eventRepo.create({
      id: randomUUID(),
      employeeId,
      nt: eventDto.nt,
      workstationId,
      eventType: eventDto.event_type,
      eventTimestamp: eventDate,
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
   * 
   * Each event must have an NT account. Events with unmapped NT accounts
   * will be rejected (409 Conflict) and counted as failed.
   * 
   * Uses bulk insert optimization for batches > 10 events.
   */
  async ingestBatchEvents(
    workstationId: string,
    events: CreateAdherenceEventDto[],
  ): Promise<{ processed: number; failed: number }> {
    if (events.length === 0) {
      return { processed: 0, failed: 0 };
    }

    // For small batches (< 10 events), use individual saves
    // For large batches (>= 10 events), use bulk insert optimization
    if (events.length < 10) {
      return this.ingestBatchEventsIndividual(workstationId, events);
    }

    return this.ingestBatchEventsBulk(workstationId, events);
  }

  /**
   * Ingest batch events individually (for small batches)
   */
  private async ingestBatchEventsIndividual(
    workstationId: string,
    events: CreateAdherenceEventDto[],
  ): Promise<{ processed: number; failed: number }> {
    let processed = 0;
    let failed = 0;

    for (const eventDto of events) {
      try {
        await this.ingestEvent(workstationId, eventDto);
        processed++;
      } catch (error) {
        failed++;
        // Log error but continue processing other events
        // 409 Conflict errors indicate unmapped NT accounts
        if (error instanceof ConflictException) {
          this.logger.warn(`Failed to ingest event - unmapped NT account: ${eventDto.nt}`);
        } else {
          this.logger.error(`Failed to ingest event: ${error.message}`, error.stack);
        }
      }
    }

    return { processed, failed };
  }

  /**
   * Ingest batch events using bulk insert optimization
   * 
   * Groups events by NT account, resolves employee IDs in batch,
   * then performs bulk insert for better performance.
   */
  private async ingestBatchEventsBulk(
    workstationId: string,
    events: CreateAdherenceEventDto[],
  ): Promise<{ processed: number; failed: number }> {
    // Step 1: Validate all events and resolve employee IDs
    const validEvents: AgentAdherenceEvent[] = [];
    const failedEvents: CreateAdherenceEventDto[] = [];
    const ntToEmployeeIdMap = new Map<string, string>();

    // Collect unique NT accounts
    const uniqueNts = [...new Set(events.map(e => e.nt).filter(Boolean))];

    // Resolve all NT accounts in batch
    for (const nt of uniqueNts) {
      try {
        const employeeId = await this.resolveEmployeeIdFromNt(nt);
        ntToEmployeeIdMap.set(nt, employeeId);
      } catch (error) {
        // NT not mapped - mark all events with this NT as failed
        this.logger.warn(`Failed to resolve NT account: ${nt}`);
      }
    }

    // Step 2: Create event entities for valid events
    for (const eventDto of events) {
      try {
        // Validate NT is present
        if (!eventDto.nt) {
          failedEvents.push(eventDto);
          continue;
        }

        // Check if NT is mapped
        const employeeId = ntToEmployeeIdMap.get(eventDto.nt);
        if (!employeeId) {
          failedEvents.push(eventDto);
          continue;
        }

        // Support both 'timestamp' and 'event_timestamp' field names
        const timestamp = eventDto.event_timestamp || eventDto.timestamp;
        if (!timestamp) {
          failedEvents.push(eventDto);
          continue;
        }

        const eventDate = new Date(timestamp);
        if (isNaN(eventDate.getTime())) {
          failedEvents.push(eventDto);
          continue;
        }

        // Create event entity
        const event = this.eventRepo.create({
          id: randomUUID(),
          employeeId,
          nt: eventDto.nt,
          workstationId,
          eventType: eventDto.event_type,
          eventTimestamp: eventDate,
          applicationName: eventDto.application_name,
          applicationPath: eventDto.application_path,
          windowTitle: eventDto.window_title,
          isWorkApplication: eventDto.is_work_application,
          metadata: eventDto.metadata || {},
        });

        validEvents.push(event);
      } catch (error) {
        failedEvents.push(eventDto);
        this.logger.error(`Failed to create event entity: ${error.message}`, error.stack);
      }
    }

    // Step 3: Bulk insert valid events
    if (validEvents.length > 0) {
      try {
        await this.eventRepo.save(validEvents);
        this.logger.log(`Bulk inserted ${validEvents.length} events`);
      } catch (error) {
        // If bulk insert fails, fall back to individual saves
        this.logger.warn(`Bulk insert failed, falling back to individual saves: ${error.message}`);
        for (const event of validEvents) {
          try {
            await this.eventRepo.save(event);
          } catch (saveError) {
            failedEvents.push(events[validEvents.indexOf(event)]);
            this.logger.error(`Failed to save event: ${saveError.message}`);
          }
        }
      }
    }

    return {
      processed: validEvents.length,
      failed: failedEvents.length,
    };
  }
}

