import {
  IsUUID,
  IsString,
  IsOptional,
  MaxLength,
} from 'class-validator';

export class RegisterWorkstationDto {
  @IsUUID()
  employee_id: string;

  @IsString()
  @MaxLength(255)
  @IsOptional()
  workstation_name?: string;

  @IsString()
  @MaxLength(100)
  @IsOptional()
  os_version?: string;

  @IsString()
  @MaxLength(50)
  @IsOptional()
  agent_version?: string;

  @IsString()
  @IsOptional()
  notes?: string;
}

