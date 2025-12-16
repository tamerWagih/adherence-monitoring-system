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
   */
  async ingestBatchEvents(
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
}

