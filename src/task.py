import asyncio
import json
import uuid
from pathlib import Path

from pydantic import BaseModel
from typing import Optional

from src.config import CONFIG_FILE
from src.file_operator import FileOperator
from src.storage.upstream_local import mutate_local_task_config


class Task(BaseModel):
    task_name: str
    stable_task_id: Optional[str] = None
    order: Optional[int] = None
    enabled: bool
    keyword: str
    description: str
    max_pages: int
    personal_only: bool
    min_price: Optional[str] = None
    max_price: Optional[str] = None
    price_sort_order: Optional[str] = "desc"
    cron: Optional[str] = None
    ai_prompt_base_file: str
    ai_prompt_criteria_file: str
    bayes_profile: Optional[str] = "bayes_v1"
    is_running: Optional[bool] = False
    generating_ai_criteria: Optional[bool] = False  # New field for AI criteria generation status
    bound_account: Optional[str] = None
    auto_switch_on_risk: Optional[bool] = False
    free_shipping: Optional[bool] = False
    new_publish_option: Optional[str] = None
    region: Optional[str] = None
    inspection_service: Optional[bool] = False
    account_assurance: Optional[bool] = False
    super_shop: Optional[bool] = False
    brand_new: Optional[bool] = False
    strict_selected: Optional[bool] = False
    resale: Optional[bool] = False


class TaskUpdate(BaseModel):
    task_name: Optional[str] = None
    order: Optional[int] = None
    enabled: Optional[bool] = None
    keyword: Optional[str] = None
    description: Optional[str] = None
    max_pages: Optional[int] = None
    personal_only: Optional[bool] = None
    min_price: Optional[str] = None
    max_price: Optional[str] = None
    price_sort_order: Optional[str] = None
    cron: Optional[str] = None
    ai_prompt_base_file: Optional[str] = None
    ai_prompt_criteria_file: Optional[str] = None
    bayes_profile: Optional[str] = None
    is_running: Optional[bool] = None
    generating_ai_criteria: Optional[bool] = None  # New field for AI criteria generation status
    bound_account: Optional[str] = None
    auto_switch_on_risk: Optional[bool] = None
    free_shipping: Optional[bool] = None
    new_publish_option: Optional[str] = None
    region: Optional[str] = None
    inspection_service: Optional[bool] = None
    account_assurance: Optional[bool] = None
    super_shop: Optional[bool] = None
    brand_new: Optional[bool] = None
    strict_selected: Optional[bool] = None
    resale: Optional[bool] = None


async def add_task(task: Task) -> bool:
    """
    向配置文件中添加一个新任务。
    """
    def transform(config_data: list[dict]) -> tuple[tuple[str, int, str], bool]:
        import re

        original_name = task.task_name
        base_name = original_name
        copy_count = 0
        match = re.match(r'^(.+?)(?:\s+\((副本)(\d+)?\))?$', original_name)
        if match:
            base_name = match.group(1)
            if match.group(3):
                copy_count = int(match.group(3))
            elif match.group(2):
                copy_count = 1
        while True:
            current_name = (original_name if copy_count == 0 else
                            f"{base_name} (副本{copy_count})")
            if not any(existing['task_name'] == current_name for existing in config_data):
                break
            copy_count += 1
        order = len(config_data) if task.order is None else task.order
        identity = str(uuid.uuid4())
        created = task.model_dump()
        created.update(task_name=current_name, order=order, stable_task_id=identity)
        config_data.append(created)
        return (current_name, order, identity), True

    name, order, identity = await asyncio.to_thread(
        mutate_local_task_config, Path(CONFIG_FILE), transform, create_if_missing=True)
    task.task_name, task.order, task.stable_task_id = name, order, identity
    return True


async def update_task(task_id: int, task: Task | dict) -> bool:
    """
    更新配置文件中指定ID的任务。
    """
    def transform(config_data: list[dict]) -> tuple[bool, bool]:
        if not 0 <= task_id < len(config_data):
            return False, False
        stable_task_id = config_data[task_id].get("stable_task_id")
        payload = task.model_dump() if hasattr(task, 'model_dump') else dict(task)
        if stable_task_id is None:
            payload.pop("stable_task_id", None)
        else:
            payload["stable_task_id"] = stable_task_id
        config_data[task_id] = payload
        return True, True

    return await asyncio.to_thread(mutate_local_task_config, Path(CONFIG_FILE),
                                   transform, create_if_missing=True)


async def get_task(task_id: int) -> Task | None:
    """
    从配置文件中获取指定ID的任务。
    """
    config_file_op = FileOperator(CONFIG_FILE)
    config_data_str = await config_file_op.read()

    if not config_data_str:
        return None

    config_data = json.loads(config_data_str)
    if len(config_data) <= task_id:
        return None

    task_data = config_data[task_id]
    task_data.setdefault("free_shipping", False)
    task_data.setdefault("new_publish_option", None)
    task_data.setdefault("price_sort_order", "desc")
    task_data.setdefault("region", None)
    task_data.setdefault("inspection_service", False)
    task_data.setdefault("account_assurance", False)
    task_data.setdefault("super_shop", False)
    task_data.setdefault("brand_new", False)
    task_data.setdefault("strict_selected", False)
    task_data.setdefault("resale", False)
    task_data.setdefault("bound_account", None)
    task_data.setdefault("auto_switch_on_risk", False)
    task_data.setdefault("bayes_profile", "bayes_v1")

    # Convert dictionary to Task object before returning
    return Task(**task_data)


async def remove_task(task_id: int) -> bool:
    """
    从配置文件中删除指定ID的任务。
    """
    def transform(config_data: list[dict]) -> tuple[bool, bool]:
        if not 0 <= task_id < len(config_data):
            return True, False
        config_data.pop(task_id)
        return True, True

    return await asyncio.to_thread(mutate_local_task_config, Path(CONFIG_FILE),
                                   transform, create_if_missing=True)
