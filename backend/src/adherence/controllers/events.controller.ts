import {
  Controller,
  Post,
  Body,
  UseGuards,
  Request,
  HttpCode,
  HttpStatus,
} from '@nestjs/common';
import { ThrottlerGuard } from '@nestjs/throttler';
import { EventIngestionService } from '../services/event-ingestion.service';
import { WorkstationAuthGuard } from '../../guards/workstation-auth.guard';
import { CreateAdherenceEventDto, BatchEventsDto } from '../../dto/create-adherence-event.dto';

/**
 * EventsController
 * 
 * Handles event ingestion from Desktop Agents.
 * Protected by WorkstationAuthGuard (API key authentication).
 */
@Controller('events')
@UseGuards(WorkstationAuthGuard, ThrottlerGuard)
export class EventsController {
  constructor(private eventIngestionService: EventIngestionService) {}

  /**
   * POST /api/adherence/events
   * 
   * Ingest a single event or batch of events from Desktop Agent.
   * 
   * Headers Required:
   * - X-API-Key: API key
   * - X-Workstation-ID: Workstation ID
   */
  @Post()
  @HttpCode(HttpStatus.OK)
  async ingestEvents(
    @Body() body: CreateAdherenceEventDto | BatchEventsDto,
    @Request() req: any,
  ) {
    const employeeId = req.employeeId;
    const workstationId = req.workstation.workstationId;

    // Check if it's a batch request
    if ('events' in body && Array.isArray(body.events)) {
      const batchDto = body as BatchEventsDto;
      const result = await this.eventIngestionService.ingestBatchEvents(
        employeeId,
        workstationId,
        batchDto.events,
      );

      return {
        success: true,
        message: 'Events ingested successfully',
        events_processed: result.processed,
        events_failed: result.failed,
      };
    } else {
      // Single event
      const eventDto = body as CreateAdherenceEventDto;
      await this.eventIngestionService.ingestEvent(
        employeeId,
        workstationId,
        eventDto,
      );

      return {
        success: true,
        message: 'Event ingested successfully',
        events_processed: 1,
        events_failed: 0,
      };
    }
  }
}

