import {
  Entity,
  PrimaryGeneratedColumn,
  Column,
  CreateDateColumn,
  UpdateDateColumn,
  Index,
  ManyToOne,
  JoinColumn,
} from 'typeorm';

/**
 * Agent Adherence Summary Entity
 * 
 * Daily aggregated adherence data for each agent.
 * Calculated by the Adherence Calculation Engine.
 */
@Entity('agent_adherence_summaries')
@Index('idx_summaries_employee_date', ['employeeId', 'scheduleDate'])
@Index('idx_summaries_date', ['scheduleDate'])
export class AgentAdherenceSummary {
  @PrimaryGeneratedColumn('uuid')
  id: string;

  @Column({ name: 'employee_id', type: 'uuid' })
  @Index()
  employeeId: string;

  @Column({ name: 'schedule_date', type: 'date' })
  scheduleDate: Date;

  // Scheduled times
  @Column({ name: 'scheduled_start_time', type: 'time', nullable: true })
  scheduledStartTime?: string;

  @Column({ name: 'scheduled_end_time', type: 'time', nullable: true })
  scheduledEndTime?: string;

  @Column({ name: 'scheduled_duration_minutes', type: 'int', default: 0 })
  scheduledDurationMinutes: number;

  // Actual times
  @Column({ name: 'actual_start_time', type: 'timestamptz', nullable: true })
  actualStartTime?: Date;

  @Column({ name: 'actual_end_time', type: 'timestamptz', nullable: true })
  actualEndTime?: Date;

  @Column({ name: 'actual_duration_minutes', type: 'int', default: 0 })
  actualDurationMinutes: number;

  // Variances
  @Column({ name: 'start_variance_minutes', type: 'int', default: 0 })
  startVarianceMinutes: number;

  @Column({ name: 'end_variance_minutes', type: 'int', default: 0 })
  endVarianceMinutes: number;

  // Break compliance
  @Column({ name: 'break_compliance_percentage', type: 'decimal', precision: 5, scale: 2, nullable: true })
  breakCompliancePercentage?: number;

  @Column({ name: 'missed_breaks_count', type: 'int', default: 0 })
  missedBreaksCount: number;

  @Column({ name: 'extended_breaks_count', type: 'int', default: 0 })
  extendedBreaksCount: number;

  // Activity metrics
  @Column({ name: 'productive_time_minutes', type: 'int', default: 0 })
  productiveTimeMinutes: number;

  @Column({ name: 'idle_time_minutes', type: 'int', default: 0 })
  idleTimeMinutes: number;

  @Column({ name: 'away_time_minutes', type: 'int', default: 0 })
  awayTimeMinutes: number;

  @Column({ name: 'non_work_app_time_minutes', type: 'int', default: 0 })
  nonWorkAppTimeMinutes: number;

  // Overall adherence
  @Column({ name: 'adherence_percentage', type: 'decimal', precision: 5, scale: 2, nullable: true })
  adherencePercentage?: number;

  // Note: exceptions_count column doesn't exist in database schema
  // Database has exception_adjustments JSONB instead
  // Setting select: false to prevent TypeORM from trying to select non-existent column
  @Column({ name: 'exceptions_count', type: 'int', nullable: true, select: false })
  exceptionsCount?: number;

  @Column({ name: 'calculated_at', type: 'timestamptz', nullable: true })
  calculatedAt?: Date;

  @CreateDateColumn({ name: 'created_at' })
  createdAt: Date;

  @UpdateDateColumn({ name: 'updated_at' })
  updatedAt: Date;
}

