import {
  Entity,
  PrimaryColumn,
  Column,
  CreateDateColumn,
  Index,
} from 'typeorm';

/**
 * Agent Adherence Event Entity
 * 
 * Note: This table is PARTITIONED by month in the database.
 * The composite primary key is (id, event_timestamp) to support partitioning.
 * 
 * This entity represents raw events captured by Desktop Agents.
 */
@Entity('agent_adherence_events')
@Index('idx_events_employee_timestamp', ['employeeId', 'eventTimestamp'])
@Index('idx_events_timestamp', ['eventTimestamp'])
@Index('idx_events_workstation', ['workstationId'])
@Index('idx_events_type', ['eventType'])
export class AgentAdherenceEvent {
  @PrimaryColumn({ type: 'uuid' })
  id: string;

  @PrimaryColumn({ name: 'event_timestamp', type: 'timestamptz' })
  eventTimestamp: Date;

  @Column({ name: 'employee_id', type: 'uuid', nullable: true })
  @Index()
  employeeId?: string; // Resolved from nt at ingestion time

  @Column({ name: 'workstation_id', type: 'varchar', length: 100, nullable: true })
  workstationId?: string;

  @Column({ name: 'nt', type: 'varchar', length: 100, nullable: true })
  @Index('idx_events_nt_timestamp', ['nt', 'eventTimestamp'])
  nt?: string; // Windows NT account (sam_account_name only, e.g., z.salah.3613)

  @Column({
    name: 'event_type',
    type: 'varchar',
    length: 50,
  })
  eventType: string; // LOGIN, LOGOFF, IDLE_START, IDLE_END, BREAK_START, BREAK_END, WINDOW_CHANGE, APPLICATION_START, APPLICATION_END, CALL_START, CALL_END

  @Column({ name: 'application_name', type: 'varchar', length: 255, nullable: true })
  applicationName?: string;

  @Column({ name: 'application_path', type: 'varchar', length: 500, nullable: true })
  applicationPath?: string;

  @Column({ name: 'window_title', type: 'varchar', length: 500, nullable: true })
  windowTitle?: string;

  @Column({ name: 'is_work_application', type: 'boolean', nullable: true })
  isWorkApplication?: boolean;

  @Column({ name: 'metadata', type: 'jsonb', nullable: true })
  metadata?: Record<string, any>;

  @CreateDateColumn({ name: 'created_at' })
  createdAt: Date;
}

