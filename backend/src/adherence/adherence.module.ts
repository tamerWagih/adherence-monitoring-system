import { Module } from '@nestjs/common';
import { TypeOrmModule } from '@nestjs/typeorm';
import { AgentAdherenceEvent } from '../entities/agent-adherence-event.entity';
import { AgentAdherenceSummary } from '../entities/agent-adherence-summary.entity';
import { AgentAdherenceException } from '../entities/agent-adherence-exception.entity';
import { AgentWorkstationConfiguration } from '../entities/agent-workstation-configuration.entity';
import { ApplicationClassification } from '../entities/application-classification.entity';
import { EmployeePersonalInfo } from '../entities/employee-personal-info.entity';
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
 * 
 * Note: Rate limiting is configured globally in AppModule.
 * Per-workstation rate limiting (10 req/min) will be implemented
 * in Week 5 using custom throttler storage or guard.
 */
@Module({
  imports: [
    TypeOrmModule.forFeature([
      AgentAdherenceEvent,
      AgentAdherenceSummary,
      AgentAdherenceException,
      AgentWorkstationConfiguration,
      ApplicationClassification,
      EmployeePersonalInfo, // For NT account resolution
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

/**
 * Note on Rate Limiting:
 * 
 * Global rate limiting is configured in AppModule (1000 req/min).
 * Per-workstation rate limiting (10 req/min per workstation) will be
 * implemented in Week 5 using:
 * - Custom throttler storage (Redis-based) with workstation_id as key
 * - Or custom guard that checks rate limits per workstation
 * 
 * For now, global rate limiting is sufficient for Week 2.
 */

