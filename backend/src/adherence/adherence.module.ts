import { Module } from '@nestjs/common';
import { TypeOrmModule } from '@nestjs/typeorm';
import { ThrottlerModule } from '@nestjs/throttler';
import { AgentAdherenceEvent } from '../entities/agent-adherence-event.entity';
import { AgentAdherenceSummary } from '../entities/agent-adherence-summary.entity';
import { AgentAdherenceException } from '../entities/agent-adherence-exception.entity';
import { AgentWorkstationConfiguration } from '../entities/agent-workstation-configuration.entity';
import { ApplicationClassification } from '../entities/application-classification.entity';
import { EventsController } from './controllers/events.controller';
import { WorkstationConfigController } from './controllers/workstation-config.controller';
import { EventIngestionService } from './services/event-ingestion.service';
import { WorkstationConfigService } from './services/workstation-config.service';
import { WorkstationAuthService } from './services/workstation-auth.service';

/**
 * AdherenceModule
 * 
 * Handles:
 * - Event ingestion from Desktop Agents
 * - Workstation configuration
 * - Adherence calculation (to be implemented)
 * - Adherence summaries (to be implemented)
 */
@Module({
  imports: [
    TypeOrmModule.forFeature([
      AgentAdherenceEvent,
      AgentAdherenceSummary,
      AgentAdherenceException,
      AgentWorkstationConfiguration,
      ApplicationClassification,
    ]),
    // Per-workstation rate limiting: 10 requests/minute
    ThrottlerModule.forFeature([
      {
        name: 'workstation',
        ttl: 60000, // 1 minute
        limit: 10, // 10 requests per minute per workstation
      },
    ]),
  ],
  controllers: [EventsController, WorkstationConfigController],
  providers: [
    EventIngestionService,
    WorkstationConfigService,
    WorkstationAuthService,
  ],
  exports: [WorkstationAuthService], // Export for use in guards
})
export class AdherenceModule {}

