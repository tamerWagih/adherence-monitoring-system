import { Injectable } from '@nestjs/common';
import { InjectRepository } from '@nestjs/typeorm';
import { Repository } from 'typeorm';
import { AgentWorkstationConfiguration } from '../../entities/agent-workstation-configuration.entity';
import * as bcrypt from 'bcrypt';

/**
 * WorkstationAuthService
 * 
 * Handles workstation authentication via API keys.
 * API keys are stored as bcrypt hashes in the database.
 */
@Injectable()
export class WorkstationAuthService {
  constructor(
    @InjectRepository(AgentWorkstationConfiguration)
    private workstationRepo: Repository<AgentWorkstationConfiguration>,
  ) {}

  /**
   * Validate API key for a workstation
   * @param workstationId UUID workstation ID
   * @param apiKey Plain text API key (43 characters)
   * @returns Workstation configuration if valid, null otherwise
   */
  async validateApiKey(
    workstationId: string,
    apiKey: string,
  ): Promise<AgentWorkstationConfiguration | null> {
    const workstation = await this.workstationRepo.findOne({
      where: { workstationId, isActive: true },
    });

    if (!workstation) {
      return null;
    }

    // Verify API key hash using bcrypt
    const isValid = await bcrypt.compare(apiKey, workstation.apiKeyHash);

    if (!isValid) {
      return null;
    }

    return workstation;
  }

  /**
   * Update last_seen_at timestamp for workstation
   * @param workstationId UUID workstation ID
   */
  async updateLastSeen(workstationId: string): Promise<void> {
    await this.workstationRepo.update(
      { workstationId },
      { lastSeenAt: new Date() },
    );
  }

  /**
   * Generate a new API key (43 characters, base64url encoded)
   * @returns Plain text API key
   */
  generateApiKey(): string {
    const { randomBytes } = require('crypto');
    const keyBytes = randomBytes(32);
    return keyBytes.toString('base64url');
  }

  /**
   * Hash API key using bcrypt
   * @param apiKey Plain text API key
   * @returns Bcrypt hash
   */
  async hashApiKey(apiKey: string): Promise<string> {
    const saltRounds = 12;
    return bcrypt.hash(apiKey, saltRounds);
  }
}

