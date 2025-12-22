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
import { Employee } from './employee.entity';
import { User } from './user.entity';

/**
 * Agent Adherence Exception Entity
 * 
 * Exception requests created by agents and approved/rejected by supervisors.
 * 
 * Note: Exception management (create, approve, reject) is handled by People Ops Backend.
 * Adherence Backend only reads these for calculations.
 */
@Entity('agent_adherence_exceptions')
@Index('idx_exceptions_employee_date', ['employeeId', 'exceptionDate'])
@Index('idx_exceptions_status', ['status'])
export class AgentAdherenceException {
  @PrimaryGeneratedColumn('uuid')
  id: string;

  @Column({ name: 'employee_id', type: 'uuid' })
  @Index()
  employeeId: string;

  @ManyToOne(() => Employee, { nullable: true })
  @JoinColumn({ name: 'employee_id' })
  employee?: Employee;

  @Column({
    name: 'exception_type',
    type: 'varchar',
    length: 50,
  })
  exceptionType: string; // LATE_START, EARLY_END, MISSED_BREAK, EXTENDED_BREAK, TECHNICAL_ISSUE, OTHER

  @Column({ name: 'exception_date', type: 'date' })
  exceptionDate: Date;

  @Column({
    name: 'status',
    type: 'varchar',
    length: 20,
    default: 'PENDING',
  })
  status: string; // PENDING, APPROVED, REJECTED

  @Column({ name: 'justification', type: 'text' })
  justification: string;

  @Column({ name: 'requested_adjustment_minutes', type: 'int', nullable: true })
  requestedAdjustmentMinutes?: number;

  @Column({ name: 'approved_adjustment_minutes', type: 'int', nullable: true })
  approvedAdjustmentMinutes?: number;

  @Column({ name: 'created_by', type: 'uuid', nullable: true })
  createdBy?: string;

  @ManyToOne(() => User, { nullable: true })
  @JoinColumn({ name: 'created_by' })
  createdByUser?: User;

  @Column({ name: 'reviewed_by', type: 'uuid', nullable: true })
  reviewedBy?: string;

  @ManyToOne(() => User, { nullable: true })
  @JoinColumn({ name: 'reviewed_by' })
  reviewedByUser?: User;

  @Column({ name: 'review_notes', type: 'text', nullable: true })
  reviewNotes?: string;

  @Column({ name: 'reviewed_at', type: 'timestamptz', nullable: true })
  reviewedAt?: Date;

  @CreateDateColumn({ name: 'created_at' })
  createdAt: Date;

  @UpdateDateColumn({ name: 'updated_at' })
  updatedAt: Date;
}

