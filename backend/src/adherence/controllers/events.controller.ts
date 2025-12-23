import {
  Controller,
  Post,
  Body,
  UseGuards,
  UseFilters,
  Request,
  HttpCode,
  HttpStatus,
  Logger,
  BadRequestException,
} from '@nestjs/common';
import {
  ApiBody,
  ApiConsumes,
  ApiExtraModels,
  ApiOperation,
  ApiResponse,
  ApiSecurity,
  ApiTags,
  getSchemaPath,
} from '@nestjs/swagger';
import { ThrottleExceptionFilter } from '../../common/filters/throttle-exception.filter';
import { ThrottlerGuard } from '@nestjs/throttler';
import { EventIngestionService } from '../services/event-ingestion.service';
import { EventIngestionQueue } from '../queues/event-ingestion.queue';
import { WorkstationAuthGuard } from '../../guards/workstation-auth.guard';
import { WorkstationRateLimitGuard } from '../../guards/workstation-rate-limit.guard';
import { CreateAdherenceEventDto, BatchEventsDto } from '../../dto/create-adherence-event.dto';

/**
 * EventsController
 * 
 * Handles event ingestion from Desktop Agents.
 * Protected by WorkstationAuthGuard (API key authentication).
 * 
 * Option 2 (Always Queue): All events are queued and processed asynchronously.
 * Returns 202 Accepted immediately after queuing.
 */
@ApiTags('Events')
@Controller('events')
@UseGuards(WorkstationAuthGuard, WorkstationRateLimitGuard, ThrottlerGuard)
@UseFilters(ThrottleExceptionFilter)
@ApiSecurity('API-Key')
@ApiSecurity('Workstation-ID')
@ApiExtraModels(CreateAdherenceEventDto, BatchEventsDto)
export class EventsController {
  private readonly logger = new Logger(EventsController.name);

  constructor(
    private eventIngestionService: EventIngestionService,
    private eventIngestionQueue: EventIngestionQueue,
  ) {}

  /**
   * POST /api/adherence/events
   * 
   * Queue a single event or batch of events from Desktop Agent for asynchronous processing.
   * 
   * Each event must include an 'nt' field (Windows NT account sam_account_name).
   * Employee ID is resolved from NT account at processing time.
   * 
   * Returns 202 Accepted immediately after queuing. Events are processed asynchronously.
   * 
   * Headers Required:
   * - X-API-Key: API key
   * - X-Workstation-ID: Workstation ID
   * 
   * Response Headers:
   * - X-Queue-Mode: true (indicates queue mode is active)
   * - X-Job-Id: Job ID for tracking (optional)
   */
  @Post()
  @HttpCode(HttpStatus.ACCEPTED)
  @ApiConsumes('application/json')
  @ApiBody({
    required: true,
    schema: {
      oneOf: [
        { $ref: getSchemaPath(CreateAdherenceEventDto) },
        { $ref: getSchemaPath(BatchEventsDto) },
      ],
    },
  })
  @ApiOperation({
    summary: 'Ingest events from Desktop Agent (queued)',
    description:
      'Queues a single event or batch of events for async processing. Requires workstation API key headers.',
  })
  @ApiResponse({ status: 202, description: 'Events queued for processing' })
  @ApiResponse({ status: 400, description: 'Invalid request body / empty batch' })
  @ApiResponse({ status: 401, description: 'Invalid workstation credentials' })
  @ApiResponse({ status: 429, description: 'Rate limit exceeded' })
  async ingestEvents(
    @Body() body: CreateAdherenceEventDto | BatchEventsDto,
    @Request() req: any,
  ) {
    const workstationId = req.workstation.workstationId;
    const response = req.res;

    // Set header to indicate queue mode
    response.setHeader('X-Queue-Mode', 'true');

    // Check if it's a batch request
    if ('events' in body && Array.isArray(body.events)) {
      const batchDto = body as BatchEventsDto;

      if (batchDto.events.length === 0) {
        response.status(HttpStatus.BAD_REQUEST);
        return {
          success: false,
          message: 'Empty batch - no events to queue',
          events_queued: 0,
        };
      }

      try {
        // Queue entire batch as a single job
        const jobId = await this.eventIngestionQueue.queueBatchEvents(
          workstationId,
          batchDto.events,
        );

        response.setHeader('X-Job-Id', jobId);

        return {
          success: true,
          message: 'Events queued for processing',
          events_queued: batchDto.events.length,
          job_id: jobId,
        };
      } catch (error) {
        this.logger.error(`Failed to queue batch events: ${error.message}`, error.stack);
        response.status(HttpStatus.SERVICE_UNAVAILABLE);
        return {
          success: false,
          message: 'Failed to queue events. Queue service may be unavailable.',
          events_queued: 0,
          error: error.message,
        };
      }
    } else {
      // Single event
      const eventDto = body as CreateAdherenceEventDto;
      try {
        const jobId = await this.eventIngestionQueue.queueEvent(
          workstationId,
          eventDto,
        );

        response.setHeader('X-Job-Id', jobId);

        return {
          success: true,
          message: 'Event queued for processing',
          events_queued: 1,
          job_id: jobId,
        };
      } catch (error) {
        this.logger.error(`Failed to queue event: ${error.message}`, error.stack);
        response.status(HttpStatus.SERVICE_UNAVAILABLE);
        return {
          success: false,
          message: 'Failed to queue event. Queue service may be unavailable.',
          events_queued: 0,
          error: error.message,
        };
      }
    }
  }
}

