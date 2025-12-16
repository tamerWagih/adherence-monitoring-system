import {
  Entity,
  PrimaryGeneratedColumn,
  Column,
  CreateDateColumn,
  UpdateDateColumn,
  Index,
} from 'typeorm';

/**
 * Agent Workstation Configuration Entity
 * 
 * Stores workstation registration and API key information.
 * API keys are stored as bcrypt hashes (never plain text).
 */
@Entity('agent_workstation_configurations')
@Index('idx_workstation_id', ['workstationId'], { unique: true })
@Index('idx_workstation_employee', ['employeeId'])
export class AgentWorkstationConfiguration {
  @PrimaryGeneratedColumn('uuid')
  id: string;

  @Column({ name: 'workstation_id', type: 'varchar', length: 100, unique: true })
  workstationId: string;

  @Column({ name: 'employee_id', type: 'uuid', nullable: true })
  employeeId?: string; // Optional (legacy). Workstations are device-only, employee resolution happens via NT at event ingestion

  @Column({ name: 'api_key_hash', type: 'varchar', length: 255 })
  apiKeyHash: string; // bcrypt hash of API key

  @Column({ name: 'workstation_name', type: 'varchar', length: 255, nullable: true })
  workstationName?: string;

  @Column({ name: 'os_version', type: 'varchar', length: 100, nullable: true })
  osVersion?: string;

  @Column({ name: 'agent_version', type: 'varchar', length: 50, nullable: true })
  agentVersion?: string;

  @Column({ name: 'is_active', type: 'boolean', default: true })
  isActive: boolean;

  @Column({ name: 'last_seen_at', type: 'timestamptz', nullable: true })
  lastSeenAt?: Date;

  @Column({ name: 'last_sync_at', type: 'timestamptz', nullable: true })
  lastSyncAt?: Date;

  @Column({ name: 'last_event_at', type: 'timestamptz', nullable: true })
  lastEventAt?: Date;

  @Column({
    name: 'registration_source',
    type: 'varchar',
    length: 50,
    default: 'ADMIN',
  })
  registrationSource: string; // ADMIN, SELF_SERVICE (future)

  @CreateDateColumn({ name: 'created_at' })
  createdAt: Date;

  @UpdateDateColumn({ name: 'updated_at' })
  updatedAt: Date;
}

